using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    public enum ServerStatus { Stopped, Preparing, Starting, Running, Stopping, Crashed }

    /// <summary>Runs the Java process, reads its console, tracks players and spots common problems.</summary>
    public class MinecraftServer
    {
        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex Done = new Regex(@"\]: Done \(\d", RegexOptions.Compiled);
        private static readonly Regex Joined = new Regex(@"\]: ([A-Za-z0-9_.]{1,17}) joined the game$", RegexOptions.Compiled);
        private static readonly Regex Left = new Regex(@"\]: ([A-Za-z0-9_.]{1,17}) left the game$", RegexOptions.Compiled);
        private static readonly Regex ListLine = new Regex(@"There are (\d+) of a max of (\d+) players online:?\s*(.*)$", RegexOptions.Compiled);

        private readonly object _gate = new object();
        private readonly HashSet<string> _players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Process _proc;
        private StreamWriter _stdin;
        private volatile bool _stopRequested;
        private volatile bool _stopAcknowledged;

        public ServerStatus Status { get; private set; } = ServerStatus.Stopped;
        public DateTime StartedAt { get; private set; }
        public int ListedMaxPlayers { get; private set; }
        public string LastProblem { get; private set; }

        public Process Process => _proc;
        public bool IsAlive { get { var p = _proc; try { return p != null && !p.HasExited; } catch { return false; } } }
        public bool IsBusy => Status == ServerStatus.Preparing || Status == ServerStatus.Starting || Status == ServerStatus.Running || Status == ServerStatus.Stopping;

        /// <summary>Every console line from the server (raised on a background thread).</summary>
        public event Action<string> OutputLine;
        public event Action StatusChanged;
        public event Action PlayersChanged;
        /// <summary>(exitCode, crashed) - raised on a background thread.</summary>
        public event Action<int, bool> Exited;

        public List<string> Players { get { lock (_gate) return _players.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); } }

        public void SetStatus(ServerStatus s)
        {
            if (Status == s) return;
            Status = s;
            try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.WriteFileOnly("status handler failed: " + ex); }
        }

        /// <param name="launchArgs">What starts the server software, e.g. "-jar server.jar --nogui"
        /// or (Forge/NeoForge) "@libraries/.../win_args.txt --nogui".</param>
        public void Start(string javaExe, int ramMB, IEnumerable<string> javaFlags, IList<string> launchArgs)
        {
            if (IsAlive) throw new FriendlyException("The server is already running.");
            if (launchArgs == null || launchArgs.Count == 0)
                throw new FriendlyException("The server software isn't installed yet.", "It will be installed on the next start.");
            foreach (var a in launchArgs)
            {
                var file = a.StartsWith("@") ? a.Substring(1) : a.EndsWith(".jar") ? a : null;
                if (file != null && !File.Exists(Path.Combine(AppPaths.Server, file)))
                    throw new FriendlyException(file + " is missing.", "It will be downloaded again on the next start.");
            }

            var args = new List<string> { "-Xms" + ramMB + "M", "-Xmx" + ramMB + "M" };
            args.AddRange(javaFlags.Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith("-Xm")));
            args.AddRange(new[]
            {
                "-Dfile.encoding=UTF-8", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8",
                "-Dterminal.jline=false", "-Dterminal.ansi=false",
            });
            args.AddRange(launchArgs);

            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = string.Join(" ", args.Select(Quote)),
                WorkingDirectory = AppPaths.Server,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            Log.WriteFileOnly("Launching: \"" + javaExe + "\" " + psi.Arguments);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => HandleLine(e.Data);
            p.ErrorDataReceived += (s, e) => HandleLine(e.Data);
            int exitHandled = 0; // Process.Exited can fire more than once on .NET Framework
            p.Exited += (s, e) => { if (Interlocked.Exchange(ref exitHandled, 1) == 0) OnExited(p); };

            lock (_gate) _players.Clear();
            LastProblem = null;
            _stopRequested = false;

            PreferUtf8WithoutBom();
            try { p.Start(); }
            catch (Exception ex) { throw new FriendlyException("Java couldn't be started.", ex.Message, ex); }

            Native.TieToThisProcess(p);
            _proc = p;
            _stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            // If Windows still made .NET send an invisible byte-order mark first, end that line now so it
            // can't spoil the first real command (otherwise "stop" arrives as "\uFEFFstop" and is ignored).
            try { if (p.StandardInput.Encoding.GetPreamble().Length > 0) _stdin.WriteLine(); } catch { }
            StartedAt = DateTime.Now;
            SetStatus(ServerStatus.Starting);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            PlayersChanged?.Invoke();
        }

        /// <summary>Types a command into the server console. Returns false if the server isn't running.</summary>
        public bool SendCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            lock (_gate)
            {
                if (_stdin == null || !IsAlive) return false;
                try { _stdin.WriteLine(command.Trim().TrimStart('/')); return true; }
                catch (Exception ex) { Log.WriteFileOnly("send command failed: " + ex.Message); return false; }
            }
        }

        /// <summary>Asks the server to save and stop; forces it closed if it hasn't stopped in time.</summary>
        public async Task StopAsync(int timeoutSeconds = 90)
        {
            var p = _proc;
            if (p == null || !IsAlive) { SetStatus(ServerStatus.Stopped); return; }
            _stopRequested = true;
            _stopAcknowledged = false;
            SetStatus(ServerStatus.Stopping);
            var started = DateTime.UtcNow;
            var lastSend = DateTime.MinValue;
            while (true)
            {
                // Paper 26.x drops (crashes on) a console command that arrives just as it finishes starting,
                // so keep asking every 10 seconds until the server says it is stopping.
                if (!_stopAcknowledged && (DateTime.UtcNow - lastSend).TotalSeconds >= 10)
                {
                    if (!SendCommand("stop")) { Kill(); break; }
                    lastSend = DateTime.UtcNow;
                }
                var exited = await Task.Run(() => { try { return p.WaitForExit(1000); } catch { return true; } }).ConfigureAwait(false);
                if (exited) return;
                // Once it's saving, give big worlds extra time before forcing it.
                if ((DateTime.UtcNow - started).TotalSeconds > (_stopAcknowledged ? timeoutSeconds * 2 : timeoutSeconds))
                {
                    Log.Warn("The server didn't stop in time - forcing it closed.");
                    Kill();
                    break;
                }
            }
            await Task.Run(() => { try { p.WaitForExit(15000); } catch { } }).ConfigureAwait(false);
        }

        public void Kill()
        {
            _stopRequested = true;
            try { var p = _proc; if (p != null && !p.HasExited) p.Kill(); } catch { }
        }

        private void OnExited(Process p)
        {
            int code = -1;
            try { p.WaitForExit(); code = p.ExitCode; } catch { }
            lock (_gate)
            {
                _players.Clear();
                try { _stdin?.Dispose(); } catch { }
                _stdin = null;
            }
            bool crashed = !_stopRequested;
            SetStatus(crashed ? ServerStatus.Crashed : ServerStatus.Stopped);
            PlayersChanged?.Invoke();
            try { Exited?.Invoke(code, crashed); } catch (Exception ex) { Log.WriteFileOnly("exit handler failed: " + ex); }
            try { p.Dispose(); } catch { }
        }

        private void HandleLine(string line)
        {
            if (line == null) return;
            line = Ansi.Replace(line, "");
            try { OutputLine?.Invoke(line); } catch { }

            if (Status == ServerStatus.Starting && Done.IsMatch(line)) SetStatus(ServerStatus.Running);
            if (_stopRequested && (line.Contains("]: Stopping server") || line.Contains("Stopping the server"))) _stopAcknowledged = true;

            Match m;
            bool changed = false;
            if ((m = Joined.Match(line)).Success) { lock (_gate) changed = _players.Add(m.Groups[1].Value); }
            else if ((m = Left.Match(line)).Success) { lock (_gate) changed = _players.Remove(m.Groups[1].Value); }
            else if ((m = ListLine.Match(line)).Success)
            {
                ListedMaxPlayers = int.Parse(m.Groups[2].Value);
                lock (_gate)
                {
                    _players.Clear();
                    foreach (var name in m.Groups[3].Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) _players.Add(name);
                }
                changed = true;
            }
            if (changed) PlayersChanged?.Invoke();

            var problem = DiagnoseLine(line);
            if (problem != null && problem != LastProblem)
            {
                LastProblem = problem;
                Log.Error(problem);
            }
        }

        /// <summary>Recognises common server errors and explains them in plain English.</summary>
        public static string DiagnoseLine(string line)
        {
            if (line.Contains("You need to agree to the EULA"))
                return "The Minecraft EULA hasn't been accepted. Press Start again and agree to it.";
            if (line.Contains("FAILED TO BIND TO PORT") || line.Contains("Address already in use"))
                return "Another program is already using the server's port. Close any other Minecraft server, or change the port in Server Settings.";
            if (line.Contains("UnsupportedClassVersionError"))
                return "A plugin or the server needs a newer Java. Delete the \"runtime\" folder and start again so CardboardBox can fetch the right Java.";
            if (line.Contains("java.lang.OutOfMemoryError"))
                return "The server ran out of memory. Give it more RAM in Server Settings (or remove heavy plugins).";
            if (line.Contains("Could not reserve enough space") || line.Contains("Invalid maximum heap size") || line.Contains("Could not create the Java Virtual Machine"))
                return "Java couldn't get the amount of RAM chosen. Lower the RAM in Server Settings.";
            if (line.Contains("session.lock") && line.Contains("already"))
                return "The world is already open in another server. Close the other server (or restart the PC) and try again.";
            var missing = System.Text.RegularExpressions.Regex.Match(line, @"Mod '([^']+)'.*requires.* of ([\w-]+), which is missing");
            if (missing.Success)
                return "The mod \"" + missing.Groups[1].Value + "\" needs another mod (\"" + missing.Groups[2].Value + "\") that isn't installed. " +
                       "Add it on the Plugins & Mods page, or remove \"" + missing.Groups[1].Value + "\".";
            if (line.IndexOf("missing dependenc", StringComparison.OrdinalIgnoreCase) >= 0 || line.Contains("Unknown dependency"))
                return "A plugin needs another plugin that isn't installed (" + line.Substring(Math.Max(0, line.LastIndexOf(':') + 1)).Trim() + "). " +
                       "Add it on the Plugins & Mods page. The server keeps running without that plugin.";
            if (line.Contains("Could not load 'plugins") || line.Contains("Error occurred while enabling"))
                return "A plugin failed to load - check the Console page for which one. The server keeps running without it.";
            return null;
        }

        /// <summary>
        /// .NET Framework opens a started program's input with the console's encoding. When that is UTF-8
        /// (Windows' "Beta: Use Unicode UTF-8" setting, or a UTF-8 console), it first writes a byte-order mark,
        /// which Minecraft reads as part of the first command. Telling .NET to use UTF-8 without a mark avoids that.
        /// </summary>
        private static void PreferUtf8WithoutBom()
        {
            try
            {
                var field = typeof(Console).GetField("inputEncoding", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                            ?? typeof(Console).GetField("_inputEncoding", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, new UTF8Encoding(false));
            }
            catch { /* the fallback in Start handles it */ }
        }

        private static string Quote(string a) => a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
    }
}
