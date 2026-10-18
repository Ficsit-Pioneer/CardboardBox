using System;
using System.IO;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>The GitHub half of the controller: hourly checks, applying updates, pushing changes, app updates.</summary>
    public partial class AppController
    {
        private bool _updateRestartScheduled;

        public CommitInfo LatestCommit { get; private set; }
        public DateTime NextCheck { get; private set; } = DateTime.MaxValue;
        public bool RepoUpdatePending { get; private set; }
        public ReleaseInfo AppUpdate { get; private set; }

        public void ScheduleNextCheck() => NextCheck = DateTime.Now.AddMinutes(Math.Max(5, Settings.CheckIntervalMinutes));

        /// <summary>After a failed check, try again in 5 minutes instead of waiting a whole interval.</summary>
        private void RetrySoon() => NextCheck = DateTime.Now.AddMinutes(Math.Min(5, Math.Max(1, Settings.CheckIntervalMinutes)));

        /// <summary>Called by a timer every few seconds; runs the hourly check when it is due.</summary>
        public void Tick()
        {
            Stats.Sample(Server.IsAlive ? Server.Process : null);
            if (DateTime.Now >= NextCheck) _ = CheckForUpdatesAsync(false);
        }

        /// <summary>Downloads the newest commit into cache\repo (does not touch the server). True if it differs from what's applied.</summary>
        private async Task<bool> DownloadLatestFromGitHubAsync()
        {
            var known = LatestCommit ?? (State.AppliedSha == null ? null
                : new CommitInfo { Sha = State.AppliedSha, Message = State.AppliedMessage, Date = State.AppliedDate });
            var commit = await GitHub.GetLatestCommitAsync(known);
            LatestCommit = commit;
            State.LastCheckUtc = DateTime.UtcNow;

            if (commit.Sha != State.SnapshotSha || !Directory.Exists(AppPaths.RepoSnapshot))
            {
                Log.Info("Downloading server files from GitHub (commit " + commit.ShortSha + ")...");
                await GitHub.DownloadSnapshotAsync(commit.Sha, Progress);
                State.SnapshotSha = commit.Sha;
            }
            Manifest = Manifest.LoadFromSnapshot();
            State.LastCheckResult = commit.Sha == State.AppliedSha ? "Up to date" : "New changes found";
            State.Save();
            return commit.Sha != State.AppliedSha;
        }

        /// <summary>Copies the downloaded GitHub files into the server folder (server must be stopped for jars).</summary>
        private void ApplySnapshot()
        {
            bool isNew = State.AppliedSha != State.SnapshotSha;
            var result = RepoSync.Apply(State);
            RepoSync.ApplyDisabledPlugins(Settings);
            ServerProperties.BuildLive(Settings);
            if (File.Exists(AppPaths.OverrideIcon))
                File.Copy(AppPaths.OverrideIcon, Path.Combine(AppPaths.Server, "server-icon.png"), true);

            if (isNew || result.Changed)
            {
                var sha = State.SnapshotSha != null && State.SnapshotSha.Length >= 7 ? State.SnapshotSha.Substring(0, 7) : State.SnapshotSha;
                Log.Success("Applied GitHub commit " + sha +
                            (LatestCommit?.Sha == State.SnapshotSha && !string.IsNullOrEmpty(LatestCommit.Message) ? " \"" + LatestCommit.Message + "\"" : "") +
                            " (" + result.Summary + ").");
                foreach (var f in result.Added) Log.WriteFileOnly("  + " + f);
                foreach (var f in result.Updated) Log.WriteFileOnly("  * " + f);
                foreach (var f in result.Removed) Log.WriteFileOnly("  - " + f);
            }

            if (LatestCommit != null && LatestCommit.Sha == State.SnapshotSha)
            {
                State.AppliedMessage = LatestCommit.Message;
                State.AppliedDate = LatestCommit.Date;
            }
            State.AppliedSha = State.SnapshotSha;
            State.LastCheckResult = "Up to date";
            State.Save();
            RepoUpdatePending = false;
        }

        /// <summary>The hourly check (or the "Check now" button).</summary>
        public async Task CheckForUpdatesAsync(bool manual)
        {
            ScheduleNextCheck();
            if (Server.Status == ServerStatus.Preparing || Server.Status == ServerStatus.Starting || Server.Status == ServerStatus.Stopping)
            {
                NextCheck = DateTime.Now.AddMinutes(2); // busy right now - try again shortly
                return;
            }
            if (!await _op.WaitAsync(0))
            {
                NextCheck = DateTime.Now.AddMinutes(2);
                if (manual) Log.Info("CardboardBox is busy - will check GitHub again in a moment.");
                return;
            }

            bool restartForUpdate = false;
            try
            {
                SetBusy("Checking GitHub for changes...");
                bool changed = await DownloadLatestFromGitHubAsync();
                if (!changed)
                {
                    if (manual) Log.Info("Already up to date with GitHub (commit " + LatestCommit.ShortSha + ").");
                    else Log.WriteFileOnly("INFO hourly check: up to date (" + LatestCommit.ShortSha + ")");
                }
                else if (!Server.IsAlive)
                {
                    Log.Info("New changes on GitHub: \"" + LatestCommit.Message + "\" - applying them now.");
                    ApplySnapshot();
                    // Also fetch new plugins / Paper builds now, so the next start is instant.
                    try { await PrepareAsync(); }
                    catch (Exception ex) { Log.Warn("Some downloads didn't finish (they'll be retried when the server starts): " + Errors.Friendly(ex)); }
                }
                else
                {
                    RepoUpdatePending = true;
                    Log.Info("New changes on GitHub: \"" + LatestCommit.Message + "\". They will be applied when the server restarts.");
                    restartForUpdate = Settings.AutoRestartForUpdates;
                }
            }
            catch (Exception ex)
            {
                State.LastCheckResult = "Failed: " + Errors.Friendly(ex);
                State.Save();
                RetrySoon();
                Log.Warn("Couldn't check GitHub (will try again later): " + Errors.Friendly(ex));
            }
            finally
            {
                ClearBusy();
                _op.Release();
            }

            if (restartForUpdate) _ = RestartForUpdateAsync();
            await CheckForAppUpdateAsync(manual);
        }

        /// <summary>Warns players in-game, then restarts so a GitHub update takes effect.</summary>
        private async Task RestartForUpdateAsync()
        {
            if (_updateRestartScheduled) return;
            _updateRestartScheduled = true;
            try
            {
                if (Server.Players.Count > 0)
                {
                    Server.SendCommand("say [CardboardBox] The server is getting an update and will restart in 60 seconds.");
                    await Task.Delay(30000);
                    Server.SendCommand("say [CardboardBox] Restarting in 30 seconds...");
                    await Task.Delay(20000);
                    Server.SendCommand("say [CardboardBox] Restarting in 10 seconds...");
                    await Task.Delay(10000);
                }
                if (!Server.IsAlive || !RepoUpdatePending) return;
                Log.Info("Restarting the server to apply the GitHub update...");
                await RestartAsync(Settings.BackupBeforeUpdates);
            }
            finally { _updateRestartScheduled = false; }
        }

        // ------------------------------------------------------------------ pushing to GitHub

        /// <summary>Uploads the settings changed in the app (server.properties + icon) to the GitHub repo.</summary>
        public async Task PushSettingsToGitHubAsync()
        {
            var repoProps = ServerProperties.Load(ServerProperties.RepoPath);
            foreach (var kv in Settings.PropertyOverrides) repoProps.Set(kv.Key, kv.Value);
            var tmp = Path.Combine(AppPaths.Cache, "push-server.properties");
            repoProps.Save(tmp);

            SetBusy("Uploading settings to GitHub...");
            try
            {
                await GitHub.PutFileAsync("server/server.properties", File.ReadAllBytes(tmp), "Update server settings (from CardboardBox)");
                if (File.Exists(AppPaths.OverrideIcon))
                    await GitHub.PutFileAsync("server/server-icon.png", File.ReadAllBytes(AppPaths.OverrideIcon), "Update server icon (from CardboardBox)");

                Settings.PropertyOverrides.Clear();
                Settings.Save();
                if (File.Exists(AppPaths.OverrideIcon)) File.Delete(AppPaths.OverrideIcon);
                Log.Success("Settings uploaded to GitHub - every CardboardBox using this repo will get them.");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
                ClearBusy();
            }
            await CheckForUpdatesAsync(false);
        }

        public async Task UploadPluginToGitHubAsync(string jarPath)
        {
            var info = new FileInfo(jarPath);
            if (info.Length > 50L * 1024 * 1024)
                throw new FriendlyException("That plugin is bigger than 50 MB - too big to upload this way.", "Add it to cardboardbox.json as a download link instead.");
            SetBusy("Uploading " + info.Name + " to GitHub...");
            try
            {
                await GitHub.PutFileAsync("server/plugins/" + info.Name, File.ReadAllBytes(jarPath), "Add plugin " + info.Name + " (from CardboardBox)");
                Log.Success("Uploaded " + info.Name + " to GitHub.");
            }
            finally { ClearBusy(); }
        }

        // ------------------------------------------------------------------ app self-update

        public async Task CheckForAppUpdateAsync(bool manual)
        {
            try
            {
                var rel = await GitHub.GetLatestReleaseAsync();
                if (SelfUpdate.IsNewer(rel))
                {
                    if (AppUpdate == null || AppUpdate.Tag != rel.Tag)
                        Log.Info("A new version of CardboardBox (" + rel.Tag + ") is available - see the GitHub page.");
                    AppUpdate = rel;
                }
                else if (manual) Log.Info("CardboardBox is up to date (version " + AppPaths.AppVersionText + ").");
                RaiseChanged();
            }
            catch (Exception ex)
            {
                if (manual) Log.Warn("Couldn't check for CardboardBox updates: " + Errors.Friendly(ex));
            }
        }

        public async Task InstallAppUpdateAsync()
        {
            if (AppUpdate == null) return;
            if (Server.IsAlive) await StopAsync();
            try { await SelfUpdate.InstallAndRestartAsync(AppUpdate, Progress); }
            finally { ClearBusy(); }
        }

        // ------------------------------------------------------------------ repo settings

        public void ChangeRepo(string owner, string name, string branch)
        {
            owner = (owner ?? "").Trim();
            name = (name ?? "").Trim();
            branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
            if (owner.Length == 0 || name.Length == 0) throw new FriendlyException("Owner and repo name can't be empty.");
            bool changed = owner != Settings.RepoOwner || name != Settings.RepoName || branch != Settings.Branch;
            Settings.RepoOwner = owner;
            Settings.RepoName = name;
            Settings.Branch = branch;
            Settings.Save();
            if (changed)
            {
                State.SnapshotSha = null; // force a fresh download from the new repo
                State.Save();
                Log.Info("Now using GitHub repo " + Settings.RepoSlug + " (" + branch + ").");
            }
        }

        public static string Describe(CommitInfo c) =>
            c == null ? "not checked yet" : c.ShortSha + (string.IsNullOrEmpty(c.Message) ? "" : " - " + c.Message);
    }
}
