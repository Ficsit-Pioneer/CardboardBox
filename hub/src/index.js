// CardboardBox Hub
//
// Every CardboardBox server connects OUT to this Worker over a WebSocket (/agent), so no port
// forwarding is needed. The admin panel (public/) connects to /admin with the ADMIN_KEY secret.
// One Durable Object ("Hub") holds all connections and passes messages between them.
//
// Agent -> hub:  hello {secret,name,version} | status {s} | console {lines} | result {req,ok,msg}
// Hub -> agent:  welcome {admins,watched} | admins {n} | watch {on} | cmd {req,action,text}
// Admin -> hub:  auth {key} | watch {id} | cmd {id,req,action,text} | forget {id}
// Hub -> admin:  ready | denied {msg} | instances {list} | status {id,s,at} | online {id,name} |
//                offline {id} | console {id,lines} | result {id,req,ok,msg} | forgotten {id}

import { DurableObject } from "cloudflare:workers";

const MAX_MESSAGE = 256 * 1024;   // bigger messages are ignored
const MAX_INSTANCES = 100;        // stops strangers filling the list
const ACTIONS = new Set(["start", "stop", "restart", "console", "backup", "check"]);

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/agent" || url.pathname === "/admin") {
      if (request.headers.get("Upgrade") !== "websocket")
        return new Response("Expected a WebSocket connection.", { status: 426 });
      return env.HUB.getByName("hub").fetch(request);
    }
    if (url.pathname === "/health")
      return Response.json({ ok: true, passwordSet: Boolean(env.ADMIN_KEY) });
    return new Response("Not found", { status: 404 });
  },
};

export class Hub extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => {
      ctx.storage.sql.exec(`CREATE TABLE IF NOT EXISTS instances (
        id TEXT PRIMARY KEY,
        secret_hash TEXT NOT NULL,
        name TEXT NOT NULL,
        first_seen INTEGER NOT NULL,
        last_seen INTEGER NOT NULL,
        status TEXT
      )`);
    });
    // Keep-alive pings are answered without waking the Durable Object.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request) {
    const url = new URL(request.url);
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    if (url.pathname === "/agent") {
      const id = (url.searchParams.get("id") || "").toLowerCase();
      if (!/^[0-9a-f-]{32,36}$/.test(id)) return new Response("Bad instance id", { status: 400 });
      this.ctx.acceptWebSocket(server, ["agent", "id:" + id]);
      server.serializeAttachment({ role: "agent", id, authed: false });
    } else {
      this.ctx.acceptWebSocket(server, ["admin"]);
      server.serializeAttachment({ role: "admin", authed: false, watch: null });
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, raw) {
    if (typeof raw !== "string" || raw.length > MAX_MESSAGE) return;
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }
    if (!msg || typeof msg.t !== "string") return;

    const att = ws.deserializeAttachment() || {};
    if (att.role === "agent") return this.fromAgent(ws, att, msg);
    if (att.role === "admin") return this.fromAdmin(ws, att, msg);
  }

  async webSocketClose(ws, code, reason) {
    const att = ws.deserializeAttachment() || {};
    try { ws.close(code, reason); } catch { /* already closed */ }
    this.afterDisconnect(ws, att);
  }

  async webSocketError(ws) {
    this.afterDisconnect(ws, ws.deserializeAttachment() || {});
  }

  // ------------------------------------------------------------------ agents (CardboardBox servers)

  async fromAgent(ws, att, msg) {
    if (!att.authed) {
      if (msg.t !== "hello" || typeof msg.secret !== "string" || msg.secret.length < 16) return ws.close(4001, "hello expected");
      const name = clean(msg.name, 60) || "CardboardBox";
      const hash = await sha256(msg.secret);
      const now = Date.now();
      const row = this.ctx.storage.sql.exec("SELECT secret_hash FROM instances WHERE id = ?", att.id).toArray()[0];
      if (row) {
        if (!safeEqual(row.secret_hash, hash)) return ws.close(4003, "wrong secret for this id");
        this.ctx.storage.sql.exec("UPDATE instances SET name = ?, last_seen = ? WHERE id = ?", name, now, att.id);
      } else {
        const count = this.ctx.storage.sql.exec("SELECT COUNT(*) AS n FROM instances").one().n;
        if (count >= MAX_INSTANCES) return ws.close(4029, "hub is full");
        this.ctx.storage.sql.exec(
          "INSERT INTO instances (id, secret_hash, name, first_seen, last_seen, status) VALUES (?, ?, ?, ?, ?, NULL)",
          att.id, hash, name, now, now);
      }

      // Only one live connection per server: drop an older one.
      for (const other of this.ctx.getWebSockets("id:" + att.id))
        if (other !== ws) { try { other.close(4000, "replaced"); } catch { } }

      att.authed = true;
      att.name = name;
      ws.serializeAttachment(att);
      send(ws, { t: "welcome", admins: this.admins().length, watched: this.isWatched(att.id) });
      this.toAdmins({ t: "online", id: att.id, name, version: clean(msg.version, 20) });
      return;
    }

    const now = Date.now();
    switch (msg.t) {
      case "status": {
        if (typeof msg.s !== "object" || msg.s === null) return;
        const json = JSON.stringify(msg.s).slice(0, 32000);
        this.ctx.storage.sql.exec("UPDATE instances SET status = ?, last_seen = ? WHERE id = ?", json, now, att.id);
        this.toAdmins({ t: "status", id: att.id, s: msg.s, at: now });
        break;
      }
      case "console": {
        if (!Array.isArray(msg.lines)) return;
        const lines = msg.lines.slice(-500).map(l => clean(l, 2000));
        for (const a of this.admins()) if ((a.deserializeAttachment() || {}).watch === att.id) send(a, { t: "console", id: att.id, lines });
        break;
      }
      case "result":
        this.toAdmins({ t: "result", id: att.id, req: clean(msg.req, 40), ok: Boolean(msg.ok), msg: clean(msg.msg, 2000) });
        break;
    }
  }

  // ------------------------------------------------------------------ admins (the panel)

  async fromAdmin(ws, att, msg) {
    if (!att.authed) {
      const key = this.env.ADMIN_KEY;
      if (msg.t !== "auth" || typeof msg.key !== "string") return ws.close(4001, "auth expected");
      if (!key) { send(ws, { t: "denied", msg: "No admin password is set yet. Run: npx wrangler secret put ADMIN_KEY" }); return ws.close(4003, "no password"); }
      if (!safeEqual(await sha256(msg.key), await sha256(key))) {
        await new Promise(r => setTimeout(r, 1000)); // slow down guessing
        send(ws, { t: "denied", msg: "Wrong password." });
        return ws.close(4003, "wrong password");
      }
      att.authed = true;
      ws.serializeAttachment(att);
      send(ws, { t: "ready" });
      send(ws, { t: "instances", list: this.list() });
      this.toAgents({ t: "admins", n: this.admins().length });
      return;
    }

    switch (msg.t) {
      case "watch": {
        const before = att.watch;
        att.watch = typeof msg.id === "string" ? msg.id : null;
        ws.serializeAttachment(att);
        if (before && before !== att.watch && !this.isWatched(before)) this.toAgent(before, { t: "watch", on: false });
        if (att.watch) this.toAgent(att.watch, { t: "watch", on: true });
        break;
      }
      case "cmd": {
        const req = clean(msg.req, 40);
        if (typeof msg.id !== "string" || !ACTIONS.has(msg.action)) return send(ws, { t: "result", id: msg.id, req, ok: false, msg: "Unknown action." });
        const delivered = this.toAgent(msg.id, { t: "cmd", req, action: msg.action, text: clean(msg.text, 500) });
        if (!delivered) send(ws, { t: "result", id: msg.id, req, ok: false, msg: "That server is offline." });
        break;
      }
      case "forget": {
        if (typeof msg.id !== "string") return;
        this.ctx.storage.sql.exec("DELETE FROM instances WHERE id = ?", msg.id);
        for (const a of this.ctx.getWebSockets("id:" + msg.id)) { try { a.close(4004, "forgotten by admin"); } catch { } }
        this.toAdmins({ t: "forgotten", id: msg.id });
        break;
      }
    }
  }

  // ------------------------------------------------------------------ helpers

  afterDisconnect(ws, att) {
    if (att.role === "agent" && att.authed) {
      // A replacement connection may already be live.
      const stillOnline = this.ctx.getWebSockets("id:" + att.id).some(o => o !== ws && (o.deserializeAttachment() || {}).authed);
      if (!stillOnline) {
        this.ctx.storage.sql.exec("UPDATE instances SET last_seen = ? WHERE id = ?", Date.now(), att.id);
        this.toAdmins({ t: "offline", id: att.id });
      }
    } else if (att.role === "admin" && att.authed) {
      if (att.watch && !this.isWatched(att.watch, ws)) this.toAgent(att.watch, { t: "watch", on: false });
      this.toAgents({ t: "admins", n: this.admins(ws).length });
    }
  }

  admins(except) {
    return this.ctx.getWebSockets("admin").filter(w => w !== except && (w.deserializeAttachment() || {}).authed);
  }

  agentSocket(id) {
    return this.ctx.getWebSockets("id:" + id).find(w => (w.deserializeAttachment() || {}).authed);
  }

  isWatched(id, except) {
    return this.admins(except).some(a => (a.deserializeAttachment() || {}).watch === id);
  }

  toAdmins(payload) { for (const a of this.admins()) send(a, payload); }

  toAgents(payload) {
    for (const w of this.ctx.getWebSockets("agent")) if ((w.deserializeAttachment() || {}).authed) send(w, payload);
  }

  toAgent(id, payload) {
    const w = this.agentSocket(id);
    if (!w) return false;
    send(w, payload);
    return true;
  }

  list() {
    const online = new Set(this.ctx.getWebSockets("agent")
      .map(w => w.deserializeAttachment() || {}).filter(a => a.authed).map(a => a.id));
    return this.ctx.storage.sql.exec("SELECT id, name, first_seen, last_seen, status FROM instances ORDER BY name").toArray()
      .map(r => ({ id: r.id, name: r.name, online: online.has(r.id), firstSeen: r.first_seen, lastSeen: r.last_seen, s: r.status ? JSON.parse(r.status) : null }));
  }
}

function send(ws, payload) {
  try { ws.send(JSON.stringify(payload)); } catch { /* socket closing */ }
}

function clean(value, max) {
  if (value === undefined || value === null) return "";
  return String(value).slice(0, max);
}

async function sha256(text) {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function safeEqual(a, b) {
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
