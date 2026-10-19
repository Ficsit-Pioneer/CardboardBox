// CardboardBox admin panel. Talks to the hub over one WebSocket.
// Everything a server sends is shown with textContent (never as HTML).
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const KEY_STORE = "cbx-admin-key";
  const servers = new Map();      // id -> { id, name, online, lastSeen, s, console: [] }
  let ws = null, authed = false, password = null, selected = null, retry = 0, pingTimer = null;
  let reqCounter = 0;

  // ------------------------------------------------------------------ connection

  function hubUrl() {
    return (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/admin";
  }

  function setConn(text, kind) {
    const el = $("conn");
    el.textContent = text;
    el.className = "pill " + kind;
  }

  function connect() {
    if (ws) { try { ws.close(); } catch { } }
    setConn("Connecting...", "warn");
    ws = new WebSocket(hubUrl());
    ws.onopen = () => ws.send(JSON.stringify({ t: "auth", key: password }));
    ws.onmessage = (e) => {
      if (e.data === "pong") return;
      let msg;
      try { msg = JSON.parse(e.data); } catch { return; }
      handle(msg);
    };
    ws.onclose = () => {
      clearInterval(pingTimer);
      if (!authed) return; // a refused login shows its own message
      setConn("Reconnecting...", "warn");
      for (const s of servers.values()) s.online = false;
      renderList(); renderDetail();
      retry = Math.min(retry + 1, 6);
      setTimeout(connect, 1000 * Math.pow(2, retry - 1));
    };
  }

  function sendMsg(obj) {
    if (ws && ws.readyState === WebSocket.OPEN) { ws.send(JSON.stringify(obj)); return true; }
    return false;
  }

  function handle(msg) {
    switch (msg.t) {
      case "ready":
        authed = true; retry = 0;
        setConn("Connected", "ok");
        $("login").hidden = true; $("app").hidden = false; $("logout").hidden = false;
        clearInterval(pingTimer);
        pingTimer = setInterval(() => { if (ws.readyState === WebSocket.OPEN) ws.send("ping"); }, 30000);
        if (selected) sendMsg({ t: "watch", id: selected });
        break;
      case "denied":
        authed = false;
        try { localStorage.removeItem(KEY_STORE); } catch { }
        setConn("Not connected", "off");
        $("login").hidden = false; $("app").hidden = true; $("logout").hidden = true;
        $("loginError").textContent = msg.msg || "Wrong password.";
        break;
      case "instances":
        for (const i of msg.list) {
          const old = servers.get(i.id);
          servers.set(i.id, { ...i, console: old ? old.console : [] });
        }
        renderList(); renderDetail();
        break;
      case "online":
      case "status":
      case "offline": {
        let s = servers.get(msg.id);
        if (!s) { s = { id: msg.id, name: msg.name || "CardboardBox", console: [] }; servers.set(msg.id, s); }
        if (msg.t === "online") { s.online = true; if (msg.name) s.name = msg.name; }
        if (msg.t === "offline") { s.online = false; s.lastSeen = Date.now(); }
        if (msg.t === "status") { s.online = true; s.s = msg.s; s.lastSeen = msg.at; }
        renderList();
        if (msg.id === selected) renderDetail();
        if (msg.t === "online" && msg.id === selected) sendMsg({ t: "watch", id: selected });
        break;
      }
      case "console": {
        const s = servers.get(msg.id);
        if (!s) return;
        s.console.push(...msg.lines);
        if (s.console.length > 1500) s.console.splice(0, s.console.length - 1500);
        if (msg.id === selected) appendConsole(msg.lines);
        break;
      }
      case "result":
        if (msg.id === selected) showNote((msg.ok ? "✔ " : "✖ ") + msg.msg);
        break;
      case "forgotten":
        servers.delete(msg.id);
        if (selected === msg.id) selected = null;
        renderList(); renderDetail();
        break;
    }
  }

  // ------------------------------------------------------------------ server list

  function statusKind(s) {
    if (!s.online) return "off";
    const st = s.s && s.s.status;
    if (st === "Running") return "ok";
    if (st === "Crashed") return "bad";
    if (st === "Starting" || st === "Stopping" || st === "Preparing") return "warn";
    return "off";
  }

  function statusText(s) {
    if (!s.online) return "Offline";
    return (s.s && s.s.status) || "Online";
  }

  function renderList() {
    const box = $("servers");
    box.textContent = "";
    const list = [...servers.values()].sort((a, b) => (b.online - a.online) || String(a.name).localeCompare(b.name));
    for (const s of list) {
      const btn = document.createElement("button");
      btn.className = "server" + (s.id === selected ? " active" : "");
      const row = el("div", "row");
      const name = el("b"); name.textContent = s.name || "CardboardBox";
      const dot = el("span", "dot " + statusKind(s));
      row.append(name, dot);
      const sub = el("small");
      const st = s.s || {};
      sub.textContent = statusText(s) +
        (s.online && st.players ? " · " + st.players.length + "/" + (st.maxPlayers || "?") + " players" : "") +
        (st.installed ? " · " + st.installed : "") +
        (!s.online && s.lastSeen ? " · seen " + ago(s.lastSeen) : "");
      btn.append(row, sub);
      btn.onclick = () => select(s.id);
      box.append(btn);
    }
    $("count").textContent = list.length ? list.filter(s => s.online).length + " of " + list.length + " online" : "";
    $("empty").hidden = list.length > 0;
  }

  function select(id) {
    selected = id;
    sendMsg({ t: "watch", id });
    renderList();
    renderDetail(true);
  }

  // ------------------------------------------------------------------ detail view

  let detailFor = null;

  function renderDetail(rebuild) {
    const host = $("detail");
    const s = selected && servers.get(selected);
    if (!s) {
      detailFor = null;
      host.textContent = "";
      const p = el("p", "muted pick"); p.textContent = servers.size ? "Pick a server on the left." : "";
      host.append(p);
      return;
    }
    if (rebuild || detailFor !== s.id) {
      host.textContent = "";
      host.append($("detailTemplate").content.cloneNode(true));
      detailFor = s.id;
      host.querySelectorAll("[data-a]").forEach(b => b.onclick = () => action(b.dataset.a));
      field("cmdForm").onsubmit = (e) => {
        e.preventDefault();
        const input = field("cmd");
        const text = input.value.trim();
        if (!text) return;
        action("console", text);
        appendConsole(["> " + text], "me");
        input.value = "";
      };
      const con = field("console");
      con.textContent = "";
      appendConsole(s.console);
    }

    const st = s.s || {};
    set("name", s.name || "CardboardBox");
    set("sub", (st.motd ? st.motd : "") );
    const pill = field("pill");
    pill.textContent = statusText(s);
    pill.className = "pill " + statusKind(s);

    const busy = st.busy || (st.lastError && st.status !== "Running" ? "Problem: " + st.lastError : "");
    const note = field("note");
    if (!note.dataset.flash) { note.hidden = !busy; note.textContent = busy || ""; }

    const alive = s.online && (st.status === "Running" || st.status === "Starting");
    toggle("start", s.online && !alive && st.status !== "Stopping" && st.status !== "Preparing");
    toggle("stop", s.online && alive);
    toggle("restart", s.online && st.status === "Running");
    toggle("backup", s.online);
    toggle("check", s.online);
    field("cmd").disabled = !(s.online && alive);

    set("players", s.online && st.players ? st.players.length + " / " + (st.maxPlayers || "?") : "-");
    set("playerNames", st.players && st.players.length ? st.players.join(", ") : (s.online ? "Nobody online" : ""));
    set("cpu", alive && st.cpu != null ? Math.round(st.cpu) + "%" : "-");
    set("cpuSys", st.sysCpu != null ? "Whole PC: " + Math.round(st.sysCpu) + "%" : "");
    set("ram", alive && st.ramMB != null ? (st.ramMB / 1024).toFixed(1) + " GB" : "-");
    set("ramSys", st.ramMaxMB ? (st.ramMaxMB / 1024).toFixed(1) + " GB given · PC " + gb(st.sysRamUsedMB) + " / " + gb(st.sysRamTotalMB) + " GB" : "");
    set("uptime", alive && st.uptimeSec ? duration(st.uptimeSec) : "-");
    set("lastSeen", s.online ? "Live" : (s.lastSeen ? "Last seen " + ago(s.lastSeen) : ""));
    set("installed", st.installed || "-");
    set("crossplay", st.crossplay ? "On (Bedrock can join)" : "Off");
    set("github", st.github || "-");
    set("app", st.appVersion ? "Version " + st.appVersion : "-");
    set("pc", st.pc || "-");
    const java = st.java || {}, bed = st.bedrock || {};
    set("javaLan", java.lan || "-");
    set("javaNet", java.internet || "-");
    set("bedLan", st.crossplay ? (bed.lan || "-") : "Crossplay off");
    set("bedNet", st.crossplay ? (bed.internet || "Appears after the server starts") : "Crossplay off");
    set("consoleHint", s.online ? "Live" : "Server is offline");
  }

  function field(name) { return $("detail").querySelector('[data-f="' + name + '"]'); }
  function set(name, text) { const f = field(name); if (f) f.textContent = text == null ? "" : String(text); }
  function toggle(action, enabled) { const b = $("detail").querySelector('[data-a="' + action + '"]'); if (b) b.disabled = !enabled; }

  function appendConsole(lines, forceClass) {
    const con = field("console");
    if (!con) return;
    const atBottom = con.scrollHeight - con.scrollTop - con.clientHeight < 40;
    const frag = document.createDocumentFragment();
    for (const line of lines) {
      const span = el("span", forceClass || lineClass(line));
      span.textContent = line + "\n";
      frag.append(span);
    }
    con.append(frag);
    while (con.childNodes.length > 1500) con.removeChild(con.firstChild);
    if (atBottom || forceClass) con.scrollTop = con.scrollHeight;
  }

  function lineClass(line) {
    if (/\bWARN\]|\bWARN\b/.test(line)) return "warn";
    if (/\bERROR\]|Exception/.test(line)) return "err";
    if (line.indexOf("[CardboardBox]") === 0) return "app";
    return "";
  }

  function action(name, text) {
    const s = servers.get(selected);
    if (!s) return;
    if (name === "forget") {
      if (!confirm("Forget " + (s.name || "this server") + "? It reappears if it connects again.")) return;
      sendMsg({ t: "forget", id: s.id });
      return;
    }
    if ((name === "stop" || name === "restart") && s.s && s.s.players && s.s.players.length &&
        !confirm(s.s.players.length + " player(s) are online. " + (name === "stop" ? "Stop" : "Restart") + " the server anyway?")) return;
    const req = "r" + (++reqCounter);
    if (!sendMsg({ t: "cmd", id: s.id, req, action: name, text: text || "" })) { showNote("✖ Not connected to the hub."); return; }
    if (name !== "console") showNote("Sent: " + name + "...");
  }

  function showNote(text) {
    const note = field("note");
    if (!note) return;
    note.hidden = false;
    note.textContent = text;
    note.dataset.flash = "1";
    clearTimeout(showNote.timer);
    showNote.timer = setTimeout(() => { delete note.dataset.flash; renderDetail(); }, 6000);
  }

  // ------------------------------------------------------------------ helpers

  function el(tag, cls) { const e = document.createElement(tag); if (cls) e.className = cls; return e; }
  function gb(mb) { return mb ? (mb / 1024).toFixed(1) : "?"; }
  function duration(sec) {
    const d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
    return d ? d + "d " + h + "h" : h ? h + "h " + m + "m" : m + "m " + Math.floor(sec % 60) + "s";
  }
  function ago(ms) {
    const sec = Math.max(0, (Date.now() - ms) / 1000);
    if (sec < 60) return "just now";
    if (sec < 3600) return Math.floor(sec / 60) + " min ago";
    if (sec < 86400) return Math.floor(sec / 3600) + " h ago";
    return new Date(ms).toLocaleString();
  }

  // ------------------------------------------------------------------ start

  $("loginForm").onsubmit = (e) => {
    e.preventDefault();
    password = $("password").value;
    $("loginError").textContent = "";
    try { if ($("remember").checked) localStorage.setItem(KEY_STORE, password); else localStorage.removeItem(KEY_STORE); } catch { }
    connect();
  };
  $("logout").onclick = () => {
    try { localStorage.removeItem(KEY_STORE); } catch { }
    authed = false;
    if (ws) ws.close();
    location.reload();
  };
  setInterval(() => { if (authed) { renderList(); if (selected) renderDetail(); } }, 15000); // refresh "seen x min ago"

  try { password = localStorage.getItem(KEY_STORE); } catch { password = null; }
  if (password) connect();
})();
