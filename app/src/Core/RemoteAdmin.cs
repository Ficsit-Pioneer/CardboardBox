using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>
    /// Connects this CardboardBox to the server owner's admin hub ("adminHub" in cardboardbox.json).
    /// The connection goes OUT from this PC, so no port forwarding is needed. The owner's panel can see
    /// status and console, run Minecraft commands, start / stop / restart, back up and check GitHub -
    /// nothing outside the Minecraft server. Every remote action is shown in this PC's console too.
    /// The person running the server can switch it off in Server Settings.
    /// </summary>
    public class RemoteAdmin
    {
        private const int ConsoleBacklog = 300;

        private readonly AppController _c;
        private readonly object _gate = new object();
        private readonly LinkedList<string> _recent = new LinkedList<string>();
        private readonly List<string> _outgoing = new List<string>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cts;
        private ClientWebSocket _ws;
        private string _runningFor;     // hub URL the loop was started for
        private volatile bool _watched;
        private volatile int _admins;
        private volatile bool _dirty = true;

        public string StateText { get; private set; } = "Off";
        public bool Connected { get; private set; }

        public RemoteAdmin(AppController controller)
        {
            _c = controller;
            controller.Server.OutputLine += line => Remember(line);
            Log.Message += (level, msg) => Remember("[CardboardBox] " + msg);
            controller.Changed += () => _dirty = true;
        }

        /// <summary>The hub to use: CARDBOARDBOX_HUB (testing) or "adminHub" from cardboardbox.json.</summary>
        public string HubUrl
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("CARDBOARDBOX_HUB");
                var url = string.IsNullOrWhiteSpace(env) ? _c.Manifest.AdminHub : env;
                return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
            }
        }

        public bool Available => HubUrl != null;

        /// <summary>Starts, stops or re-targets the connection to match the settings. Safe to call often.</summary>
        public void Sync()
        {
            var want = _c.Settings.AllowRemoteAdmin ? HubUrl : null;
            if (want == null)
            {
                if (_runningFor != null) Stop();
                SetState(HubUrl == null ? "Not set up by the server owner" : "Off");
                return;
            }
            if (want == _runningFor) return;
            Stop();
            _runningFor = want;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => RunAsync(want, token));
        }

        public void Stop()
        {
            _runningFor = null;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            _ws = null;
            Connected = false;
            _watched = false;
            SetState("Off");
        }

        // ------------------------------------------------------------------ connection loop

        private async Task RunAsync(string hub, CancellationToken token)
        {
            int delay = 5;
            while (!token.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                int closeCode = 0;
                try
                {
                    SetState("Connecting...");
                    using (var ws = new ClientWebSocket())
                    {
                        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
                        var uri = new Uri(ToWebSocketUrl(hub) + "/agent?id=" + Identity().Item1);
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(20));
                            await ws.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                        }
                        _ws = ws;
                        await SendAsync(ws, new Dictionary<string, object>
                        {
                            ["t"] = "hello",
                            ["secret"] = Identity().Item2,
                            ["name"] = DisplayName,
                            ["version"] = AppPaths.AppVersionText,
                        }, token).ConfigureAwait(false);

                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            var receive = ReceiveLoopAsync(ws, linked.Token);
                            var send = SendLoopAsync(ws, linked.Token);
                            var done = await Task.WhenAny(receive, send).ConfigureAwait(false);
                            linked.Cancel();
                            if (done == receive) closeCode = await receive.ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    var root = ex;
                    while (root.InnerException != null) root = root.InnerException;
                    _lastError = root.Message.Trim().TrimEnd('.');
                    Log.WriteFileOnly("INFO remote admin connection: " + ex);
                }
                finally
                {
                    _ws = null;
                    _watched = false;
                    if (Connected) { Connected = false; }
                }
                if (token.IsCancellationRequested) break;

                if (closeCode == 4003)
                {
                    // The hub knows this PC under a different secret (settings were reset): start a fresh identity.
                    _c.Settings.RemoteId = null;
                    _c.Settings.RemoteSecret = null;
                    _c.Settings.Save();
                }
                if ((DateTime.UtcNow - started).TotalMinutes > 2) delay = 5; // it was working; retry quickly
                SetState(closeCode == 4029 ? "The owner's admin panel is full"
                    : "Can't reach the owner's admin panel" + (_lastError != null ? " (" + _lastError + ")" : "") + " - retrying");
                if (_wasConnected) { Log.Warn("Lost the connection to the server owner's admin panel - reconnecting."); _wasConnected = false; }
                try { await Task.Delay(TimeSpan.FromSeconds(closeCode == 4029 ? 600 : delay), token).ConfigureAwait(false); } catch { break; }
                delay = Math.Min(delay * 2, 120);
            }
        }

        /// <summary>Returns the close code the hub used (0 if none).</summary>
        private async Task<int> ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close)
                            return (int)(ws.CloseStatus ?? 0);
                        ms.Write(buffer, 0, r.Count);
                        if (ms.Length > 1024 * 1024) return 0; // nonsense - reconnect
                    } while (!r.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    if (text == "pong") { _lastPong = DateTime.UtcNow; continue; }
                    Dictionary<string, object> msg;
                    try { msg = Json.ParseObject(text); } catch { continue; }
                    await HandleAsync(ws, msg, token).ConfigureAwait(false);
                }
            }
            return 0;
        }

        private DateTime _lastPong = DateTime.UtcNow;
        private string _lastError;
        private bool _wasConnected;

        private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var lastStatus = DateTime.MinValue;
            var lastPing = DateTime.UtcNow;
            _lastPong = DateTime.UtcNow;
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var now = DateTime.UtcNow;
                if (Connected)
                {
                    var interval = TimeSpan.FromSeconds(_admins > 0 ? 5 : 60);
                    if (now - lastStatus >= interval || (_dirty && _admins > 0 && now - lastStatus >= TimeSpan.FromSeconds(2)))
                    {
                        _dirty = false;
                        lastStatus = now;
                        await SendAsync(ws, new Dictionary<string, object> { ["t"] = "status", ["s"] = BuildStatus() }, token).ConfigureAwait(false);
                    }
                    if (_watched)
                    {
                        List<string> lines = null;
                        lock (_gate)
                        {
                            if (_outgoing.Count > 0) { lines = new List<string>(_outgoing); _outgoing.Clear(); }
                        }
                        if (lines != null)
                            await SendAsync(ws, new Dictionary<string, object> { ["t"] = "console", ["lines"] = lines }, token).ConfigureAwait(false);
                    }
                }
                if (now - lastPing > TimeSpan.FromSeconds(30))
                {
                    lastPing = now;
                    await SendTextAsync(ws, "ping", token).ConfigureAwait(false);
                }
                if (now - _lastPong > TimeSpan.FromSeconds(100)) return; // the hub stopped answering - reconnect
                await Task.Delay(300, token).ConfigureAwait(false);
            }
        }

        private async Task HandleAsync(ClientWebSocket ws, Dictionary<string, object> msg, CancellationToken token)
        {
            switch (msg.Str("t"))
            {
                case "welcome":
                    _admins = msg.Int("admins");
                    Connected = true;
                    _lastError = null;
                    SetState("Connected");
                    if (!_wasConnected) Log.Success("Connected to the server owner's admin panel.");
                    _wasConnected = true;
                    _dirty = true;
                    SetWatched(msg.Bool("watched"));
                    break;
                case "admins":
                    _admins = msg.Int("n");
                    _dirty = true;
                    break;
                case "watch":
                    SetWatched(msg.Bool("on"));
                    break;
                case "cmd":
                    // Run in the background: a restart can take a minute and must not block the connection.
                    var req = msg.Str("req");
                    var action = msg.Str("action");
                    var text = msg.Str("text");
                    _ = Task.Run(async () =>
                    {
                        Tuple<bool, string> result;
                        try { result = await ExecuteAsync(action, text).ConfigureAwait(false); }
                        catch (Exception ex) { result = Tuple.Create(false, Errors.Friendly(ex)); }
                        _dirty = true;
                        try { await SendAsync(ws, new Dictionary<string, object> { ["t"] = "result", ["req"] = req, ["ok"] = result.Item1, ["msg"] = result.Item2 }, token).ConfigureAwait(false); }
                        catch { /* connection closed meanwhile */ }
                    });
                    break;
            }
        }

        private void SetWatched(bool on)
        {
            lock (_gate)
            {
                _outgoing.Clear();
                if (on) _outgoing.AddRange(_recent); // send the recent history first
            }
            _watched = on;
        }

        // ------------------------------------------------------------------ commands from the owner

        private Task<Tuple<bool, string>> ExecuteAsync(string action, string text)
        {
            Log.Info("Remote admin (server owner): " + Describe(action, text));
            return _c.RunOnUiAsync(async () =>
            {
                switch (action)
                {
                    case "start":
                        if (_c.Server.IsAlive) return Tuple.Create(true, "Already running.");
                        if (!_c.Settings.EulaAccepted)
                            return Tuple.Create(false, "The Minecraft EULA hasn't been accepted on that PC yet - they need to press Start once themselves.");
                        var ok = await _c.StartAsync();
                        return Tuple.Create(ok, ok ? "Starting." : (_c.LastError ?? "Couldn't start."));
                    case "stop":
                        await _c.StopAsync();
                        return Tuple.Create(true, "Stopped.");
                    case "restart":
                        await _c.RestartAsync();
                        return Tuple.Create(_c.Server.IsAlive, _c.Server.IsAlive ? "Restarting." : (_c.LastError ?? "Couldn't restart."));
                    case "console":
                        if (string.IsNullOrWhiteSpace(text)) return Tuple.Create(false, "Empty command.");
                        return _c.Server.SendCommand(text)
                            ? Tuple.Create(true, "Sent: " + text)
                            : Tuple.Create(false, "The server isn't running.");
                    case "backup":
                        var path = await _c.BackupAsync("remote");
                        return Tuple.Create(path != null, path != null ? "Backup saved: " + Path.GetFileName(path) : "No world to back up yet, or the backup failed.");
                    case "check":
                        await _c.CheckForUpdatesAsync(true);
                        return Tuple.Create(true, "Checked GitHub.");
                    default:
                        return Tuple.Create(false, "Unknown action.");
                }
            });
        }

        private static string Describe(string action, string text)
        {
            switch (action)
            {
                case "console": return "ran \"" + text + "\"";
                case "check": return "checked GitHub for updates";
                case "backup": return "made a backup";
                default: return action + " the server";
            }
        }

        // ------------------------------------------------------------------ status

        private Dictionary<string, object> BuildStatus()
        {
            var s = _c.Server;
            var st = _c.Stats;
            bool alive = s.IsAlive;
            var lan = _c.LocalIp + (_c.Port == 25565 ? "" : ":" + _c.Port);
            var commit = _c.State.AppliedSha;
            return new Dictionary<string, object>
            {
                ["status"] = s.Status.ToString(),
                ["players"] = alive ? s.Players : new List<string>(),
                ["maxPlayers"] = _c.MaxPlayers,
                ["cpu"] = alive ? Math.Round(st.ServerCpu, 1) : 0.0,
                ["sysCpu"] = Math.Round(st.SystemCpu, 1),
                ["ramMB"] = alive ? st.ServerRamBytes / (1024 * 1024) : 0L,
                ["ramMaxMB"] = _c.RamMB,
                ["sysRamUsedMB"] = st.SystemRamUsedBytes / (1024 * 1024),
                ["sysRamTotalMB"] = st.SystemRamTotalBytes / (1024 * 1024),
                ["uptimeSec"] = alive ? (long)(DateTime.Now - s.StartedAt).TotalSeconds : 0L,
                ["installed"] = _c.DescribeInstalled(),
                ["crossplay"] = _c.CrossplayActive,
                ["github"] = commit == null ? null : commit.Substring(0, Math.Min(7, commit.Length)) +
                             (string.IsNullOrEmpty(_c.State.AppliedMessage) ? "" : " - " + _c.State.AppliedMessage),
                ["appVersion"] = AppPaths.AppVersionText,
                ["pc"] = Environment.MachineName,
                ["motd"] = PlainMotd(),
                ["busy"] = _c.BusyText,
                ["lastError"] = alive ? null : _c.LastError,
                ["java"] = new Dictionary<string, object> { ["lan"] = lan, ["internet"] = _c.InternetAddress },
                ["bedrock"] = new Dictionary<string, object>
                {
                    ["lan"] = _c.LocalIp + "  port " + GeyserConfig.BedrockPort,
                    ["internet"] = _c.Settings.BedrockInternetAddress == null ? null : _c.Settings.BedrockInternetAddress + "  port 443",
                },
            };
        }

        private string PlainMotd()
        {
            try
            {
                var motd = ServerProperties.Effective(_c.Settings, "motd", "") ?? "";
                return Regex.Replace(motd, "[\u00a7&][0-9a-fk-orA-FK-OR]", "").Replace("\n", "  ");
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ helpers

        public string DisplayName => string.IsNullOrWhiteSpace(_c.Settings.RemoteName) ? Environment.MachineName : _c.Settings.RemoteName.Trim();

        /// <summary>(id, secret) that identify this PC to the hub; created on first use.</summary>
        private Tuple<string, string> Identity()
        {
            var s = _c.Settings;
            if (string.IsNullOrEmpty(s.RemoteId) || string.IsNullOrEmpty(s.RemoteSecret))
            {
                s.RemoteId = Guid.NewGuid().ToString("D");
                var bytes = new byte[32];
                using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(bytes);
                s.RemoteSecret = string.Concat(bytes.Select(b => b.ToString("x2")));
                s.Save();
            }
            return Tuple.Create(s.RemoteId, s.RemoteSecret);
        }

        private void Remember(string line)
        {
            lock (_gate)
            {
                _recent.AddLast(line);
                while (_recent.Count > ConsoleBacklog) _recent.RemoveFirst();
                if (_watched)
                {
                    _outgoing.Add(line);
                    if (_outgoing.Count > 2000) _outgoing.RemoveRange(0, _outgoing.Count - 2000);
                }
            }
        }

        private void SetState(string text)
        {
            if (StateText == text) return;
            StateText = text;
            _c.RaiseChanged();
        }

        private static string ToWebSocketUrl(string url)
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + url.Substring(8);
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + url.Substring(7);
            return url;
        }

        private Task SendAsync(ClientWebSocket ws, Dictionary<string, object> payload, CancellationToken token) =>
            SendTextAsync(ws, Json.Write(payload), token);

        private async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }
    }
}
