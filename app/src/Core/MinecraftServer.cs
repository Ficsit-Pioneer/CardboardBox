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

        public void Start(string javaExe, int ramMB, IEnumerable<string> javaFlags)
        {
            if (IsAlive) throw new FriendlyException("The server is already running.");
            if (!File.Exists(AppPaths.ServerJar)) throw new FriendlyException("server.jar is missing.", "It will be downloaded again on the next start.");

            var args = new List<string> { "-Xms" + ramMB + "M", "-Xmx" + ramMB + "M" };
            args.AddRange(javaFlags.Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith("-Xm")));
            args.AddRange(new[]
            {
                "-Dfile.encoding=UTF-8", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8",
                "-Dterminal.jline=false", "-Dterminal.ansi=false",
                "-jar", "server.jar", "--nogui",
            });

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

            try { p.Start(); }
            catch (Exception ex) { throw new FriendlyException("Java couldn't be started.", ex.Message, ex); }

            Native.TieToThisProcess(p);
            _proc = p;
            _stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
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
            SetStatus(ServerStatus.Stopping);
            if (!SendCommand("stop")) Kill();
            var exited = await Task.Run(() => { try { return p.WaitForExit(timeoutSeconds * 1000); } catch { return true; } }).ConfigureAwait(false);
            if (!exited)
            {
                Log.Warn("The server didn't stop within " + timeoutSeconds + " seconds - forcing it closed.");
                Kill();
                await Task.Run(() => { try { p.WaitForExit(15000); } catch { } }).ConfigureAwait(false);
            }
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
            if (line.Contains("Could not load 'plugins") || line.Contains("Error occurred while enabling"))
                return "A plugin failed to load - check the Console page for which one. The server keeps running without it.";
            return null;
        }

        private static string Quote(string a) => a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
    }
}
