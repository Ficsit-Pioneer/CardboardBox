using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>
    /// The brain of CardboardBox: installs, starts, stops, restarts and syncs. The GUI only calls into this.
    /// Everything here is called on the UI thread; <see cref="Changed"/> is always raised on the UI thread.
    /// </summary>
    public partial class AppController
    {
        private readonly SynchronizationContext _ui;
        private readonly SemaphoreSlim _op = new SemaphoreSlim(1, 1);
        private readonly List<DateTime> _crashes = new List<DateTime>();
        private string _javaExe;

        public Settings Settings { get; }
        public AppState State { get; }
        public GitHub GitHub { get; }
        public MinecraftServer Server { get; } = new MinecraftServer();
        public StatsSampler Stats { get; } = new StatsSampler();
        public Manifest Manifest { get; private set; } = new Manifest();

        public string BusyText { get; private set; }
        public int BusyPercent { get; private set; } = -1;
        public string LastError { get; private set; }
        public bool RestartNeeded { get; set; }
        public string LocalIp { get; private set; } = NetInfo.LocalIPv4();
        public string PublicIp { get; private set; }

        /// <summary>Something changed - refresh the screen. Always raised on the UI thread.</summary>
        public event Action Changed;

        public AppController()
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            AppPaths.EnsureFolders();
            Settings = Settings.Load();
            State = AppState.Load();
            GitHub = new GitHub(Settings);
            try { if (File.Exists(Manifest.PathInSnapshot)) Manifest = Manifest.LoadFromSnapshot(); }
            catch (Exception ex) { Log.Warn(Errors.Friendly(ex)); }

            Server.StatusChanged += RaiseChanged;
            Server.PlayersChanged += RaiseChanged;
            Server.Exited += (code, crashed) => _ui.Post(_ => OnServerExited(code, crashed), null);
            Playit.Changed += RaiseChanged;
            Server.OutputLine += WatchGeyserOutput;
            Remote = new RemoteAdmin(this);
            Server.StatusChanged += () => { if (Server.Status == ServerStatus.Running) _ui.Post(_ => OnServerRunning(), null); };
        }

        public RemoteAdmin Remote { get; }

        /// <summary>Runs work on the UI thread (where all server control happens) and returns its result.</summary>
        public Task<T> RunOnUiAsync<T>(Func<Task<T>> work)
        {
            var tcs = new TaskCompletionSource<T>();
            _ui.Post(async _ =>
            {
                try { tcs.SetResult(await work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }

        public void RaiseChanged() => _ui.Post(_ => { try { Changed?.Invoke(); } catch (Exception ex) { Log.WriteFileOnly("UI refresh failed: " + ex); } }, null);

        // ------------------------------------------------------------------ numbers for the GUI

        public int Port
        {
            get
            {
                int.TryParse(ServerProperties.Effective(Settings, "server-port", "25565"), out var p);
                return p > 0 && p < 65536 ? p : 25565;
            }
        }

        public int MaxPlayers
        {
            get
            {
                int.TryParse(ServerProperties.Effective(Settings, "max-players", "20"), out var n);
                return n > 0 ? n : 20;
            }
        }

        public static int SystemRamMB => (int)Native.TotalMemoryMB;

        /// <summary>Most RAM we let the server have: leave at least 2 GB for Windows.</summary>
        public static int MaxSafeRamMB => Math.Max(1024, SystemRamMB - 2048);

        public int RamMB
        {
            get
            {
                int wanted = Settings.RamMB > 0 ? Settings.RamMB : Manifest.DefaultRamGB * 1024;
                return Math.Max(1024, Math.Min(wanted, MaxSafeRamMB));
            }
        }

        private void Progress(string what, long done, long total)
        {
            if (total > 0)
            {
                BusyPercent = (int)(done * 100 / total);
                BusyText = "Downloading " + what + "... " + BusyPercent + "% (" + FileUtil.FormatBytes(done) + " of " + FileUtil.FormatBytes(total) + ")";
            }
            else
            {
                BusyPercent = -1;
                BusyText = "Downloading " + what + "... " + FileUtil.FormatBytes(done);
            }
            RaiseChanged();
        }

        private void SetBusy(string text)
        {
            BusyText = text;
            BusyPercent = -1;
            RaiseChanged();
        }

        private void ClearBusy()
        {
            BusyText = null;
            BusyPercent = -1;
            RaiseChanged();
        }

        // ------------------------------------------------------------------ startup

        /// <summary>Called once when the window opens: installs everything, then optionally starts the server.</summary>
        public async Task InitAsync()
        {
            Log.Info("CardboardBox " + AppPaths.AppVersionText + " - server folder: " + AppPaths.Root);
            _ = RefreshPublicIpAsync();

            await _op.WaitAsync();
            try
            {
                await PrepareAsync();
                LastError = null;
                Log.Success("Everything is installed and up to date.");
            }
            catch (Exception ex)
            {
                LastError = Errors.Friendly(ex);
                Log.Error("Setup didn't finish", ex);
            }
            finally
            {
                ClearBusy();
                _op.Release();
            }

            ScheduleNextCheck();
            if (LastError != null) RetrySoon();
            _ = CheckForAppUpdateAsync(false);

            if (Settings.AutoStartServer && Settings.EulaAccepted && LastError == null)
            {
                Log.Info("Starting the server automatically (you can turn this off in Server Settings).");
                await StartAsync();
            }
        }

        public async Task RefreshPublicIpAsync()
        {
            LocalIp = NetInfo.LocalIPv4();
            PublicIp = await NetInfo.PublicIPv4Async();
            RaiseChanged();
        }

        /// <summary>
        /// Makes sure every file is in place: GitHub data, Paper, Java, plugins, settings.
        /// Safe to run any number of times - it only downloads what is missing or changed.
        /// </summary>
        private async Task PrepareAsync()
        {
            AppPaths.EnsureFolders();

            SetBusy("Checking GitHub for the latest server files...");
            try
            {
                await DownloadLatestFromGitHubAsync();
            }
            catch (Exception ex)
            {
                if (!Directory.Exists(AppPaths.RepoSnapshot))
                    throw new FriendlyException("Couldn't download the server files from GitHub (" + Settings.RepoSlug + ").", Errors.Friendly(ex), ex);
                Log.Warn("Couldn't reach GitHub, using the files from last time: " + Errors.Friendly(ex));
            }

            SetBusy("Copying server files...");
            ApplySnapshot();

            await InstallSoftwareAsync();

            if (Settings.EulaAccepted) WriteEula();
        }

        private static void WriteEula()
        {
            File.WriteAllText(Path.Combine(AppPaths.Server, "eula.txt"),
                "# Agreed to in CardboardBox by the person running this server (https://aka.ms/MinecraftEULA)\n" +
                "# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\neula=true\n", new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ start / stop

        /// <summary>Starts the server. Returns false (and sets LastError) if it couldn't.</summary>
        public async Task<bool> StartAsync()
        {
            if (Server.IsBusy || Server.IsAlive) return true;
            if (!Settings.EulaAccepted)
            {
                LastError = "The Minecraft EULA must be accepted first.";
                return false;
            }

            await _op.WaitAsync();
            try
            {
                Server.SetStatus(ServerStatus.Preparing);
                await PrepareAsync();

                if (!NetInfo.IsPortFree(Port))
                    throw new FriendlyException("Port " + Port + " is already in use by another program.",
                        "Close any other Minecraft server (check Task Manager for java.exe), or pick another port in Server Settings.");

                SetBusy("Starting the server...");
                var flags = new List<string>(State.JavaFlags);
                flags.AddRange(Manifest.ExtraJvmFlags);
                if (CrossplayActive) flags.AddRange(GeyserConfig.JvmFlags);
                Server.Start(_javaExe, RamMB, flags, State.LaunchArgs);
                if (Settings.KeepPcAwake) Native.KeepAwake(true);
                RestartNeeded = false;
                LastError = null;
                Log.Info("Server starting with " + (RamMB / 1024.0).ToString("0.#") + " GB of RAM. The first start takes a minute or two.");
                if (!Directory.Exists(Path.Combine(AppPaths.Server, "libraries")))
                    Log.Info("First start: if Windows asks about \"OpenJDK Platform binary\", click Allow so friends can connect.");
                _ = StartPlayitAsync();
                return true;
            }
            catch (Exception ex)
            {
                LastError = Errors.Friendly(ex);
                Log.Error("Couldn't start the server", ex);
                if (!Server.IsAlive) Server.SetStatus(ServerStatus.Stopped);
                return false;
            }
            finally
            {
                ClearBusy();
                _op.Release();
            }
        }

        public async Task StopAsync()
        {
            if (!Server.IsAlive) { Server.SetStatus(ServerStatus.Stopped); return; }
            await _op.WaitAsync();
            try
            {
                SetBusy("Stopping the server and saving the world...");
                Log.Info("Stopping the server...");
                await Server.StopAsync();
                Playit.Stop();
            }
            catch (Exception ex) { Log.Error("Problem while stopping", ex); }
            finally
            {
                Native.KeepAwake(false);
                ClearBusy();
                _op.Release();
            }
        }

        public async Task RestartAsync(bool backupFirst = false)
        {
            await StopAsync();
            if (backupFirst) await BackupAsync("before-update");
            await StartAsync();
        }

        public void Kill()
        {
            Log.Warn("Force-stopping the server (the world may lose the last few minutes).");
            Server.Kill();
        }

        public bool SendCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            if (!Server.SendCommand(command))
            {
                Log.Warn("The server isn't running, so the command wasn't sent.");
                return false;
            }
            return true;
        }

        private async void OnServerExited(int code, bool crashed)
        {
            Native.KeepAwake(false);
            Playit.Stop();
            if (!crashed)
            {
                Log.Info("Server stopped.");
                RaiseChanged();
                return;
            }

            var problem = Server.LastProblem;
            Log.Error("The server stopped unexpectedly (exit code " + code + ")." + (problem != null ? " " + problem : " See the Console page for details."));

            _crashes.Add(DateTime.Now);
            _crashes.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 10);
            bool fatal = problem != null && !problem.Contains("plugin") && !problem.Contains("ran out of memory");

            if (!Settings.AutoRestartOnCrash || fatal)
            {
                RaiseChanged();
                return;
            }
            if (_crashes.Count > 3)
            {
                Log.Error("The server crashed more than 3 times in 10 minutes, so automatic restarts are paused. Check the Console page, then press Start.");
                RaiseChanged();
                return;
            }

            Log.Info("Restarting automatically in 10 seconds...");
            await Task.Delay(10000);
            if (Server.Status == ServerStatus.Crashed) await StartAsync();
        }

        // ------------------------------------------------------------------ backups

        public async Task<string> BackupAsync(string reason = null)
        {
            try
            {
                SetBusy("Backing up the world...");
                return await Backups.CreateAsync(Server, reason, Settings.KeepBackups);
            }
            catch (Exception ex)
            {
                Log.Error("Backup failed", ex);
                return null;
            }
            finally { ClearBusy(); }
        }

        // ------------------------------------------------------------------ settings

        /// <summary>Saves server.properties changes made in the app (kept on top of the GitHub values).</summary>
        public void SaveServerProperties(IDictionary<string, string> changes)
        {
            foreach (var kv in changes)
            {
                var repoValue = ServerProperties.Load(ServerProperties.RepoPath).Get(kv.Key);
                if (repoValue != null && repoValue == kv.Value) Settings.PropertyOverrides.Remove(kv.Key);
                else Settings.PropertyOverrides[kv.Key] = kv.Value;
            }
            Settings.Save();
            if (!Server.IsAlive) ServerProperties.BuildLive(Settings);
            else RestartNeeded = true;
            RaiseChanged();
        }

        public void SetServerIcon(string pngPath)
        {
            if (pngPath == null)
            {
                if (File.Exists(AppPaths.OverrideIcon)) File.Delete(AppPaths.OverrideIcon);
                var repoIcon = Path.Combine(RepoSync.RepoServerFolder, "server-icon.png");
                var live = Path.Combine(AppPaths.Server, "server-icon.png");
                if (File.Exists(repoIcon)) File.Copy(repoIcon, live, true);
                else if (File.Exists(live)) File.Delete(live);
            }
            else
            {
                File.Copy(pngPath, AppPaths.OverrideIcon, true);
                File.Copy(pngPath, Path.Combine(AppPaths.Server, "server-icon.png"), true);
            }
            if (Server.IsAlive) RestartNeeded = true;
            RaiseChanged();
        }

        public void ResetOverrides()
        {
            Settings.PropertyOverrides.Clear();
            Settings.Save();
            SetServerIcon(null);
            if (!Server.IsAlive) ServerProperties.BuildLive(Settings);
            else RestartNeeded = true;
            Log.Info("Server settings reset to the values from GitHub.");
            RaiseChanged();
        }
    }
}
