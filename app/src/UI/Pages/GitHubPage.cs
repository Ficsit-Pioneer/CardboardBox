using System;
using System.Drawing;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>Which repo to sync from, sync status, the optional push token, and app updates.</summary>
    public class GitHubPage : Page
    {
        private readonly TextBox _owner, _repo, _branch, _token;
        private readonly NumericUpDown _interval;
        private readonly Label _applied, _checked, _next, _result, _tokenState, _appVersion;
        private readonly Button _checkNow, _installUpdate;

        public GitHubPage(MainForm main, AppController c)
            : base(main, c, "GitHub", "Your server's base files live in a public GitHub repo. CardboardBox keeps this PC in sync with it.")
        {
            // ---------- sync status
            var status = new Card("Sync status");
            _applied = Theme.Wrap("");
            _checked = Theme.Label("");
            _next = Theme.Label("");
            _result = Theme.Wrap("", Theme.Small, Theme.Muted);
            _checkNow = Theme.Primary("Check GitHub now");
            _checkNow.Click += (s, e) => Run(() => C.CheckForUpdatesAsync(true), _checkNow);
            _interval = new NumericUpDown { Minimum = 5, Maximum = 1440, Increment = 5, Width = 80, Margin = new Padding(0, 4, 6, 0) };
            _interval.ValueChanged += (s, e) =>
            {
                if (C.Settings.CheckIntervalMinutes == (int)_interval.Value) return;
                C.Settings.CheckIntervalMinutes = (int)_interval.Value;
                C.Settings.Save();
                C.ScheduleNextCheck();
            };
            var openRepo = Theme.Secondary("Open repo in browser");
            openRepo.Click += (s, e) => Theme.OpenUrl(C.Settings.RepoUrl);
            status.Add(
                FieldLabel("Server files on this PC"), _applied,
                _checked, _next, _result,
                Theme.Row(new Label { Text = "Check every", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, _interval,
                          new Label { Text = "minutes", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }),
                Theme.Row(_checkNow, openRepo));
            Place(status);

            // ---------- repo
            var repo = new Card("Repository");
            _owner = new TextBox { Width = 200 };
            _repo = new TextBox { Width = 200 };
            _branch = new TextBox { Width = 120 };
            var saveRepo = Theme.Secondary("Use this repo");
            saveRepo.Click += (s, e) => Run(async () =>
            {
                C.ChangeRepo(_owner.Text, _repo.Text, _branch.Text);
                await C.CheckForUpdatesAsync(true);
            }, saveRepo);
            var resetRepo = Theme.Link("Back to the default repo", () =>
            {
                _owner.Text = Settings.DefaultOwner;
                _repo.Text = Settings.DefaultRepo;
                _branch.Text = Settings.DefaultBranch;
            });
            repo.Add(
                Hint("Leave this alone unless the server owner tells you to change it."),
                Theme.Row(Theme.Stack(FieldLabel("Owner"), _owner), Theme.Stack(FieldLabel("Repo name"), _repo), Theme.Stack(FieldLabel("Branch"), _branch)),
                Theme.Row(saveRepo, resetRepo));
            Place(repo);

            // ---------- token
            var push = new Card("Upload changes to GitHub (optional)");
            _token = new TextBox { Width = 380, UseSystemPasswordChar = true };
            _tokenState = Theme.Label("", Theme.Bold);
            var saveToken = Theme.Secondary("Save token");
            saveToken.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_token.Text)) return;
                C.Settings.GitHubToken = _token.Text;
                C.Settings.Save();
                _token.Clear();
                Log.Success("GitHub token saved (encrypted for this Windows user).");
                C.RaiseChanged();
            };
            var removeToken = Theme.Secondary("Remove token");
            removeToken.Click += (s, e) =>
            {
                C.Settings.GitHubToken = null;
                C.Settings.Save();
                Log.Info("GitHub token removed.");
                C.RaiseChanged();
            };
            push.Add(
                Hint("Only needed if this PC should UPLOAD changes (settings, icon, plugins) to the repo. Downloading never needs a token."),
                Hint("Create a \"fine-grained token\" on GitHub with access to just this repo and the permission \"Contents: Read and write\"."),
                Theme.Link("Create a token on GitHub", () => Theme.OpenUrl("https://github.com/settings/personal-access-tokens/new")),
                FieldLabel("Token"), _token, Theme.Row(saveToken, removeToken), _tokenState);
            Place(push);

            // ---------- app updates
            var app = new Card("CardboardBox app");
            _appVersion = Theme.Label("");
            var checkApp = Theme.Secondary("Check for app update");
            checkApp.Click += (s, e) => Run(() => C.CheckForAppUpdateAsync(true), checkApp);
            _installUpdate = Theme.Primary("Install update");
            _installUpdate.Click += (s, e) => InstallAppUpdate();
            app.Add(_appVersion,
                Hint("New versions of CardboardBox are published as GitHub Releases in the same repo. Updating keeps all your server files."),
                Theme.Row(checkApp, _installUpdate));
            Place(app);

            VisibleChanged += (s, e) => { if (Visible) LoadFields(); };
        }

        private void LoadFields()
        {
            _owner.Text = C.Settings.RepoOwner;
            _repo.Text = C.Settings.RepoName;
            _branch.Text = C.Settings.Branch;
            _interval.Value = Math.Max(_interval.Minimum, Math.Min(_interval.Maximum, C.Settings.CheckIntervalMinutes));
            RefreshView();
        }

        public override void RefreshView()
        {
            var st = C.State;
            var sha = st.AppliedSha;
            _applied.Text = string.IsNullOrEmpty(sha)
                ? "Not downloaded yet."
                : "Commit " + sha.Substring(0, Math.Min(7, sha.Length)) + (string.IsNullOrEmpty(st.AppliedMessage) ? "" : " - \"" + st.AppliedMessage + "\"") +
                  (DateTime.TryParse(st.AppliedDate, out var d) ? "\nMade " + d.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm") : "");
            _checked.Text = "Last checked: " + Theme.Ago(st.LastCheckUtc);
            _result.Text = (st.LastCheckResult ?? "") + (C.RepoUpdatePending ? "\nNew files are waiting for the next server restart." : "");
            _result.ForeColor = st.LastCheckResult != null && st.LastCheckResult.StartsWith("Failed") ? Theme.Red : Theme.Muted;
            Tick();

            _tokenState.Text = C.Settings.HasToken ? "\u2714 A token is saved - uploads are enabled." : "No token saved - this PC only downloads.";
            _tokenState.ForeColor = C.Settings.HasToken ? Theme.Green : Theme.Muted;

            _appVersion.Text = "You have version " + AppPaths.AppVersionText +
                               (C.AppUpdate != null ? " \u2014 version " + C.AppUpdate.Tag.TrimStart('v') + " is available!" : ".");
            _installUpdate.Enabled = C.AppUpdate != null;
        }

        public override void Tick()
        {
            _next.Text = C.NextCheck == DateTime.MaxValue ? "Next check: after setup finishes"
                : "Next check: in " + Math.Max(0, (int)Math.Ceiling((C.NextCheck - DateTime.Now).TotalMinutes)) + " min (" + C.NextCheck.ToString("HH:mm") + ")";
        }

        public void InstallAppUpdate()
        {
            if (C.AppUpdate == null) return;
            var msg = "Update CardboardBox to " + C.AppUpdate.Tag + "?" +
                      (C.Server.IsAlive ? "\n\nThe server will be stopped (saving the world) and CardboardBox will reopen by itself." : "\n\nCardboardBox will reopen by itself.");
            if (!Confirm(msg)) return;
            Run(async () =>
            {
                await C.InstallAppUpdateAsync();
                Application.Exit();
            }, _installUpdate);
        }
    }
}
