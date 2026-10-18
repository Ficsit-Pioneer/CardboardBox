using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    public class PlayitTunnel
    {
        /// <summary>What friends type into Minecraft.</summary>
        public string Address;
        /// <summary>
        /// Same tunnel written as host:port. For reference only: playit checks the hostname inside the
        /// Minecraft handshake, so players should use <see cref="Address"/> (the game resolves the SRV record).
        /// </summary>
        public string AddressWithPort;
        public bool Disabled;
    }

    /// <summary>
    /// playit.gg integration: a free tunnel so friends can join without router port forwarding.
    ///
    /// How it works:
    ///  1. CardboardBox makes a claim code and opens https://playit.gg/claim/CODE in the browser.
    ///  2. The person logs in to playit.gg (free) and approves CardboardBox; we receive an agent secret key.
    ///  3. With that key we create a "Minecraft Java" tunnel to 127.0.0.1:port through playit's API.
    ///  4. While the server runs we run playit's official agent (playitd.exe --secret ...), which carries the traffic.
    /// </summary>
    public class Playit
    {
        private const string Api = "https://api.playit.gg";
        private const string AgentRepo = "playit-cloud/playit-agent";
        private const string AgentAsset = "playit-windows-x86_64-signed.exe";
        private const string PipeName = @"\\.\pipe\cardboardbox-playitd";

        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

        private readonly object _gate = new object();
        private Process _agent;
        private string _secret;
        private bool _wanted;
        private volatile bool _keyRejected;
        private readonly List<DateTime> _restarts = new List<DateTime>();

        public static string Folder => Path.Combine(AppPaths.Runtime, "playit");
        public static string AgentExe => Path.Combine(Folder, "playitd.exe");
        public static string ClaimUrl(string code) => "https://playit.gg/claim/" + code;
        public const string DashboardUrl = "https://playit.gg/account/tunnels";

        public bool IsRunning { get { var p = _agent; try { return p != null && !p.HasExited; } catch { return false; } } }

        /// <summary>Raised when the agent starts or stops (any thread).</summary>
        public event Action Changed;

        // ------------------------------------------------------------------ API

        /// <summary>playit answered with "fail" - a normal "not yet / not allowed" answer, not a crash.</summary>
        public class FailException : Exception
        {
            public string Reason { get; }
            public FailException(string reason) : base("playit.gg: " + reason) { Reason = reason; }
        }

        private static async Task<object> CallAsync(string path, Dictionary<string, object> body, string secret = null)
        {
            var headers = new Dictionary<string, string>();
            if (secret != null) headers["Authorization"] = "Agent-Key " + secret.Trim();
            var r = await Net.SendAsync(HttpMethod.Post, Api + path, headers, Json.Write(body ?? new Dictionary<string, object>())).ConfigureAwait(false);

            Dictionary<string, object> d;
            try { d = Json.ParseObject(r.Body); }
            catch { throw new FriendlyException("playit.gg gave an unexpected answer (HTTP " + (int)r.Status + ").", "It may be busy - try again in a minute."); }

            d.TryGetValue("data", out var data);
            switch (d.Str("status"))
            {
                case "success":
                    return data;
                case "fail":
                    throw new FailException(data is string s ? s : Json.Write(data));
                case "error":
                    var e = data as Dictionary<string, object>;
                    var type = e.Str("type");
                    var msg = e.Str("message") ?? type;
                    if (type == "auth")
                        throw new FriendlyException("playit.gg didn't accept this PC's playit key (" + msg + ").",
                            "Set up playit.gg again from the Server Settings page.");
                    throw new FriendlyException("playit.gg reported a problem: " + msg);
                default:
                    throw new FriendlyException("playit.gg gave an unexpected answer (HTTP " + (int)r.Status + ").");
            }
        }

        // ------------------------------------------------------------------ claiming (linking to an account)

        public static string NewClaimCode()
        {
            var bytes = new byte[5];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        /// <summary>Waits until the person approves CardboardBox on playit.gg, then returns the agent secret key.</summary>
        public static async Task<string> ClaimAsync(string code, string agentVersion, Action<string> status, CancellationToken token)
        {
            var setup = new Dictionary<string, object>
            {
                ["code"] = code,
                ["agent_type"] = "self-managed", // lets this key create its own tunnel
                // Must look like "playit 1.0.10": playit treats agents with an unreadable version as too old.
                ["version"] = "playit " + agentVersion,
            };

            var giveUp = DateTime.UtcNow.AddMinutes(30);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > giveUp) throw new FriendlyException("playit.gg setup timed out.", "Press Set up playit.gg to try again.");

                string state;
                try { state = Convert.ToString(await CallAsync("/claim/setup", setup).ConfigureAwait(false)); }
                catch (FriendlyException ex)
                {
                    status("Can't reach playit.gg right now, retrying... (" + ex.Message + ")");
                    await Task.Delay(4000, token).ConfigureAwait(false);
                    continue;
                }

                if (state == "UserAccepted") break;
                if (state == "UserRejected")
                    throw new FriendlyException("CardboardBox wasn't approved on playit.gg.", "If that was a mistake, press Set up playit.gg again.");
                status(state == "WaitingForUser"
                    ? "Almost there - approve CardboardBox on the playit.gg page in your browser."
                    : "Waiting for the playit.gg page to open in your browser...");
                await Task.Delay(2000, token).ConfigureAwait(false);
            }

            status("Approved! Finishing setup...");
            for (int i = 0; i < 60; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var data = await CallAsync("/claim/exchange", new Dictionary<string, object> { ["code"] = code }).ConfigureAwait(false) as Dictionary<string, object>;
                    var key = data.Str("secret_key");
                    if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
                }
                catch (FailException) { /* not ready yet */ }
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
            throw new FriendlyException("playit.gg approved CardboardBox but didn't send the key in time.", "Press Set up playit.gg to try again.");
        }

        // ------------------------------------------------------------------ tunnel

        /// <summary>
        /// Makes sure this agent has a Minecraft Java tunnel to 127.0.0.1:port (creating or fixing it if needed)
        /// and returns its public address. Returns null if playit is still allocating it.
        /// </summary>
        public static async Task<PlayitTunnel> EnsureTunnelAsync(string secret, int port)
        {
            var run = await RunDataAsync(secret).ConfigureAwait(false);
            var agentId = run.Str("agent_id");
            WarnAboutAccount(run.Str("account_status"));

            var tunnel = FindTunnel(run, port);
            if (tunnel != null)
            {
                if (tunnel.Int("local_port") != port || tunnel.Str("local_ip") != "127.0.0.1")
                {
                    await WhileAgentConnects(() => CallAsync("/tunnels/update", new Dictionary<string, object>
                    {
                        ["tunnel_id"] = tunnel.Str("id"),
                        ["local_ip"] = "127.0.0.1",
                        ["local_port"] = port,
                        ["agent_id"] = agentId,
                        ["enabled"] = true,
                    }, secret), port).ConfigureAwait(false);
                    Log.Info("Pointed the playit.gg tunnel at port " + port + ".");
                }
                return Describe(tunnel);
            }

            bool pending = run.Arr("pending").OfType<Dictionary<string, object>>().Any(t => t.Str("tunnel_type") == "minecraft-java");
            if (!pending)
            {
                await WhileAgentConnects(() => CallAsync("/tunnels/create", new Dictionary<string, object>
                    {
                        ["name"] = "CardboardBox Minecraft",
                        ["tunnel_type"] = "minecraft-java",
                        ["port_type"] = "tcp",
                        ["port_count"] = 1,
                        ["origin"] = new Dictionary<string, object>
                        {
                            ["type"] = "agent",
                            ["data"] = new Dictionary<string, object> { ["agent_id"] = agentId, ["local_ip"] = "127.0.0.1", ["local_port"] = port },
                        },
                        ["enabled"] = true,
                        ["alloc"] = null,
                        ["firewall_id"] = null,
                        ["proxy_protocol"] = null,
                    }, secret), port).ConfigureAwait(false);
                Log.Info("Created a playit.gg tunnel for Minecraft.");
            }

            // New tunnels take a few seconds to get an address.
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                tunnel = FindTunnel(await RunDataAsync(secret).ConfigureAwait(false), port);
                if (tunnel != null) return Describe(tunnel);
            }
            return null;
        }

        /// <summary>
        /// Runs a tunnel change. Right after linking, playit may answer "AgentVersionTooOld" until the
        /// agent program has connected once and reported its version, so that answer is retried for a minute.
        /// </summary>
        private static async Task WhileAgentConnects(Func<Task<object>> call, int port)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { await call().ConfigureAwait(false); return; }
                catch (FailException f) when (f.Reason == "AgentVersionTooOld" && attempt < 12)
                {
                    await Task.Delay(5000).ConfigureAwait(false);
                }
                catch (FailException f)
                {
                    throw TunnelCreateProblem(f.Reason, port);
                }
            }
        }

        /// <summary>Names the agent on playit.gg so it's recognisable in the dashboard (best effort).</summary>
        public static async Task NameAgentAsync(string secret)
        {
            try
            {
                var run = await RunDataAsync(secret).ConfigureAwait(false);
                await CallAsync("/agents/rename", new Dictionary<string, object>
                {
                    ["agent_id"] = run.Str("agent_id"),
                    ["name"] = "CardboardBox (" + Environment.MachineName + ")",
                }, secret).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.WriteFileOnly("INFO couldn't rename playit agent: " + ex.Message); }
        }

        private static async Task<Dictionary<string, object>> RunDataAsync(string secret)
        {
            var run = await CallAsync("/agents/rundata", null, secret).ConfigureAwait(false) as Dictionary<string, object>;
            if (run == null) throw new FriendlyException("playit.gg gave an unexpected answer about this agent.");
            return run;
        }

        private static Dictionary<string, object> FindTunnel(Dictionary<string, object> run, int port)
        {
            var tunnels = run.Arr("tunnels").OfType<Dictionary<string, object>>().ToList();
            return tunnels.FirstOrDefault(t => t.Str("tunnel_type") == "minecraft-java")
                   ?? tunnels.FirstOrDefault(t => t.Int("local_port") == port && t.Str("proto") != "udp");
        }

        private static PlayitTunnel Describe(Dictionary<string, object> t)
        {
            var domain = t.Str("custom_domain") ?? t.Str("assigned_domain");
            var port = t.Obj("port").Int("from");
            bool javaType = t.Str("tunnel_type") == "minecraft-java";
            return new PlayitTunnel
            {
                // Minecraft Java tunnels have an SRV record, so the bare name works in the game.
                Address = javaType ? domain : domain + ":" + port,
                AddressWithPort = domain + ":" + port,
                Disabled = t.ContainsKey("disabled") && t["disabled"] != null,
            };
        }

        private static void WarnAboutAccount(string status)
        {
            switch (status)
            {
                case "email-not-verified": Log.Warn("playit.gg: please verify your email address (check your inbox) so the tunnel keeps working."); break;
                case "agent-over-limit": Log.Warn("playit.gg: your account has too many agents. Remove old ones at playit.gg."); break;
                case "agent-disabled": Log.Warn("playit.gg: this agent is disabled on playit.gg. Turn it back on in your playit.gg dashboard."); break;
                case "banned": Log.Error("playit.gg: this account is banned, so the tunnel can't work."); break;
                case "has-message": Log.Info("playit.gg has a message for you - log in at playit.gg to read it."); break;
            }
        }

        private static FriendlyException TunnelCreateProblem(string reason, int port)
        {
            switch (reason)
            {
                case "RequiresVerifiedAccount":
                    return new FriendlyException("playit.gg needs you to verify your email before it can make a tunnel.",
                        "Check your inbox for the playit.gg email, then press Set up playit.gg again.");
                case "AgentVersionTooOld":
                    return new FriendlyException("playit.gg didn't see the CardboardBox agent come online.",
                        "Check the internet connection and press Set up playit.gg again.");
                case "RequiresPlayitPremium":
                    return new FriendlyException("playit.gg says this kind of tunnel needs playit Premium.",
                        "Create a free \"Minecraft Java\" tunnel on playit.gg by hand instead.");
                default:
                    return new FriendlyException("playit.gg couldn't create the tunnel (" + reason + ").",
                        "On playit.gg, add a \"Minecraft Java\" tunnel for the CardboardBox agent pointing to 127.0.0.1:" + port + ", then press Set up again.");
            }
        }

        // ------------------------------------------------------------------ the agent program

        /// <summary>Downloads (or updates, at most once a day) playit's official signed agent.</summary>
        public static async Task EnsureAgentAsync(AppState state, ProgressHandler progress)
        {
            bool have = File.Exists(AgentExe);
            if (have && (DateTime.UtcNow - state.PlayitCheckedUtc).TotalHours < 24) return;

            Dictionary<string, object> rel;
            try
            {
                rel = Json.ParseObject(await Net.GetStringAsync("https://api.github.com/repos/" + AgentRepo + "/releases/latest",
                    new Dictionary<string, string> { ["Accept"] = "application/vnd.github+json" }).ConfigureAwait(false));
            }
            catch (Exception) when (have)
            {
                return; // offline / rate limited: keep the agent we have
            }

            var tag = rel.Str("tag_name");
            state.PlayitCheckedUtc = DateTime.UtcNow;
            if (have && tag == state.PlayitVersion) { state.Save(); return; }

            var asset = rel.Arr("assets").OfType<Dictionary<string, object>>().FirstOrDefault(a => a.Str("name") == AgentAsset);
            if (asset == null) throw new FriendlyException("Couldn't find playit's Windows program in its latest release (" + tag + ").");
            var digest = asset.Str("digest");
            var sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7) : null;

            Log.Info("Downloading the playit.gg agent " + tag + "...");
            Directory.CreateDirectory(Folder);
            await Net.DownloadFileAsync(asset.Str("browser_download_url"), AgentExe, "playit.gg agent", progress, sha256: sha256).ConfigureAwait(false);
            state.PlayitVersion = tag;
            state.Save();
            Log.Success("Installed the playit.gg agent " + tag + ".");
        }

        /// <summary>Starts the agent (and restarts it if it stops unexpectedly) until Stop is called.</summary>
        public void Start(string secret)
        {
            lock (_gate)
            {
                _secret = secret;
                _wanted = true;
                _keyRejected = false;
                if (IsRunning) return;
                Launch();
            }
        }

        public void Stop()
        {
            Process p;
            lock (_gate)
            {
                _wanted = false;
                p = _agent;
                _agent = null;
            }
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
            Log.Info("playit.gg tunnel stopped.");
            Changed?.Invoke();
        }

        private void Launch()
        {
            if (!File.Exists(AgentExe)) throw new FriendlyException("The playit.gg agent is missing.", "It will be downloaded again.");
            var psi = new ProcessStartInfo
            {
                FileName = AgentExe,
                Arguments = "--secret " + _secret.Trim() + " --socket-path " + PipeName,
                WorkingDirectory = Folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => HandleLine(e.Data);
            p.ErrorDataReceived += (s, e) => HandleLine(e.Data);
            int handled = 0;
            p.Exited += (s, e) => { if (Interlocked.Exchange(ref handled, 1) == 0) OnExited(p); };
            p.Start();
            Native.TieToThisProcess(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _agent = p;
            Log.Info("playit.gg tunnel starting...");
            Changed?.Invoke();
        }

        private void HandleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            line = Ansi.Replace(line, "").Trim();
            Log.WriteFileOnly("playit: " + line);
            // The agent probes several tunnel servers (including IPv6 ones); failed probes are normal noise.
            bool probeNoise = line.Contains("failed to send initial ping") || line.Contains("failed to ping tunnel server");
            if (line.Contains(" ERROR ") && !probeNoise) Log.Warn("[playit] " + Shorten(line));
            else if (!_keyRejected && (line.Contains("InvalidAgentKey") || line.Contains("Invalid secret")))
            {
                _keyRejected = true; // restarting won't help
                Log.Error("playit.gg rejected this PC's key. Set up playit.gg again from Server Settings.");
            }
        }

        private static string Shorten(string line)
        {
            // "2026-10-18T22:00:00.000Z ERROR playitd::thing: message" -> "message"
            var m = Regex.Match(line, @"(?:ERROR|WARN)\s+[\w:]+:\s*(.*)$");
            return m.Success ? m.Groups[1].Value : line;
        }

        private async void OnExited(Process p)
        {
            int code = -1;
            try { p.WaitForExit(); code = p.ExitCode; } catch { }
            bool restart;
            lock (_gate)
            {
                if (_agent == p) _agent = null;
                restart = _wanted && !_keyRejected;
                _restarts.Add(DateTime.UtcNow);
                _restarts.RemoveAll(t => (DateTime.UtcNow - t).TotalMinutes > 10);
                if (_restarts.Count > 5) restart = false;
            }
            try { p.Dispose(); } catch { }
            Changed?.Invoke();
            if (!_wanted) return;

            if (_keyRejected) { lock (_gate) _wanted = false; return; }
            if (!restart)
            {
                Log.Error("The playit.gg agent keeps stopping (exit code " + code + "), so it was turned off. Check the log, or set up playit.gg again.");
                lock (_gate) _wanted = false;
                return;
            }
            Log.Warn("The playit.gg agent stopped (exit code " + code + ") - restarting it in 5 seconds.");
            await Task.Delay(5000).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_wanted || IsRunning) return;
                try { Launch(); }
                catch (Exception ex) { Log.Error("Couldn't restart the playit.gg agent", ex); }
            }
        }
    }
}
