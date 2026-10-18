using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>The home screen: status, start/stop, players, CPU, RAM, addresses, GitHub sync.</summary>
    public class DashboardPage : Page
    {
        private readonly Label _status, _statusDetail, _announcement, _activity;
        private readonly Button _start, _stop, _restart, _kill, _appUpdate;
        private readonly Panel _banner;
        private readonly Label _bannerText;
        private readonly StatTile _players, _cpu, _ram, _uptime, _version, _lan, _wan, _github;
        private readonly Card _announceCard;
        private readonly List<string> _recent = new List<string>();

        public DashboardPage(MainForm main, AppController c)
            : base(main, c, "Dashboard", "Everything about your server at a glance.", 4)
        {
            // ---------- status card
            var statusCard = new Card(null);
            _status = new Label { Text = "\u25cf  Stopped", Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, ForeColor = Theme.Gray };
            _statusDetail = new Label { Text = "", AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 10) };
            _start = Theme.Good("\u25b6  Start server");
            _stop = Theme.Secondary("\u25a0  Stop");
            _restart = Theme.Secondary("\u21bb  Restart");
            _kill = Theme.Danger("Force stop");
            _start.Click += (s, e) => Run(Main.StartServerAsync, _start);
            _stop.Click += (s, e) => Run(C.StopAsync, _stop);
            _restart.Click += (s, e) => Run(() => C.RestartAsync(), _restart);
            _kill.Click += (s, e) => { if (Confirm("Force-stopping skips saving, so the last few minutes of the world may be lost.\n\nForce stop now?")) C.Kill(); };
            statusCard.Add(_status, _statusDetail, Theme.Row(_start, _stop, _restart, _kill));
            Place(statusCard, 4);

            // ---------- banner for updates / restart needed
            _banner = new Panel { BackColor = Theme.AccentSoft, Height = 44, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(14, 0, 8, 0), Visible = false };
            _bannerText = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.AccentDark, Font = Theme.Bold };
            _appUpdate = Theme.Primary("Update CardboardBox");
            _appUpdate.Dock = DockStyle.Right;
            _appUpdate.Margin = new Padding(0, 6, 0, 6);
            _appUpdate.Click += (s, e) => Main.PageOf<GitHubPage>()?.InstallAppUpdate();
            _banner.Controls.Add(_bannerText);
            _banner.Controls.Add(_appUpdate);
            Place(_banner, 4);

            // ---------- tiles
            _players = Tile("Players online");
            _cpu = Tile("CPU", true);
            _ram = Tile("RAM", true);
            _uptime = Tile("Uptime");
            _version = Tile("Minecraft version");
            _lan = Tile("Home network address");
            _wan = Tile("Internet address");
            _github = Tile("GitHub sync");
            foreach (var t in new[] { _lan, _wan, _github, _version }) t.Value.Font = Theme.H2;

            _lan.Controls.Add(TileLink("Copy address", () => CopyText(LanAddress)));
            var wanLinks = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 24, BackColor = Color.Transparent };
            wanLinks.Controls.Add(Theme.Link("Copy", () => CopyText(WanAddress)));
            wanLinks.Controls.Add(Theme.Link("Auto port-forward", PortForward));
            _wan.Controls.Add(wanLinks);
            _github.Controls.Add(TileLink("Check now", () => Run(() => C.CheckForUpdatesAsync(true))));
            _version.Controls.Add(TileLink("Open server folder", () => Theme.OpenFolder(AppPaths.Server)));

            // ---------- announcement + activity
            _announceCard = new Card("Message from the server owner");
            _announcement = new Label { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Theme.Text };
            _announceCard.Add(_announcement, Hint("Set by \"announcement\" in cardboardbox.json on GitHub."));
            Place(_announceCard, 2);

            var activityCard = new Card("Recent activity");
            _activity = new Label { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Theme.Text, Font = Theme.Small };
            activityCard.Add(_activity, Theme.Link("Open the Console page for everything", () => Main.ShowPage(Main.ConsoleView)));
            Place(activityCard, 2);

            Log.Message += (level, msg) =>
            {
                if (IsDisposed || !IsHandleCreated) { lock (_recent) Push(level, msg); return; }
                try { BeginInvoke((Action)(() => { lock (_recent) Push(level, msg); if (Visible) UpdateActivity(); })); } catch { }
            };
        }

        private StatTile Tile(string title, bool chart = false)
        {
            var t = new StatTile(title, chart) { Height = chart ? 150 : 128, MinimumSize = new Size(0, chart ? 150 : 128) };
            Place(t);
            return t;
        }

        private static Control TileLink(string text, Action click)
        {
            var l = Theme.Link(text, click);
            l.Dock = DockStyle.Bottom;
            return l;
        }

        private void Push(LogLevel level, string msg)
        {
            var prefix = level == LogLevel.Error ? "\u2716 " : level == LogLevel.Warn ? "\u26a0 " : level == LogLevel.Success ? "\u2714 " : "\u2022 ";
            _recent.Add(DateTime.Now.ToString("HH:mm") + "  " + prefix + msg);
            while (_recent.Count > 7) _recent.RemoveAt(0);
        }

        private void UpdateActivity()
        {
            lock (_recent) _activity.Text = _recent.Count == 0 ? "Nothing yet." : string.Join("\n", _recent.AsEnumerable().Reverse());
        }

        private string LanAddress => C.LocalIp + (C.Port == 25565 ? "" : ":" + C.Port);
        private string WanAddress => C.PublicIp == null ? null : C.PublicIp + (C.Port == 25565 ? "" : ":" + C.Port);

        private void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); Log.Info("Copied " + text + " - send it to your friends so they can join."); }
            catch { MessageBox.Show(this, text, "Server address"); }
        }

        private void PortForward()
        {
            Run(async () =>
            {
                Log.Info("Asking the router to open port " + C.Port + "...");
                var result = await NetInfo.TryPortForwardAsync(C.Port);
                if (result == "OK")
                {
                    Log.Success("Port " + C.Port + " is now forwarded to this PC. Friends can join using " + (WanAddress ?? "your internet address") + ".");
                    MessageBox.Show(this, "Done! Your router now sends Minecraft traffic to this PC.\n\nFriends outside your home can join using:\n" + WanAddress,
                        "Port forwarding", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    Log.Warn(result);
                    MessageBox.Show(this, result, "Port forwarding", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });
        }

        public override void RefreshView()
        {
            var s = C.Server.Status;
            _status.Text = "\u25cf  " + MainForm.StatusText(s);
            _status.ForeColor = MainForm.StatusColor(s);

            bool alive = C.Server.IsAlive;
            bool busy = C.Server.IsBusy;
            _start.Visible = !alive;
            _start.Enabled = !busy;
            _stop.Visible = alive;
            _stop.Enabled = s != ServerStatus.Stopping;
            _restart.Visible = alive;
            _restart.Enabled = s == ServerStatus.Running;
            _kill.Visible = s == ServerStatus.Stopping || s == ServerStatus.Starting;

            string detail;
            if (C.BusyText != null) detail = C.BusyText;
            else if (s == ServerStatus.Running) detail = "Friends can join now. Address: " + LanAddress + " (same Wi-Fi)" + (WanAddress != null ? " or " + WanAddress + " (internet)" : "");
            else if (s == ServerStatus.Starting) detail = "Loading the world - this takes a minute or two (longer the very first time).";
            else if (s == ServerStatus.Crashed) detail = C.Server.LastProblem ?? "The server stopped unexpectedly. See the Console page.";
            else if (C.LastError != null) detail = C.LastError;
            else detail = C.Settings.EulaAccepted ? "Press Start to open the server." : "Press Start - you'll be asked to accept the Minecraft EULA the first time.";
            _statusDetail.Text = detail;
            _statusDetail.ForeColor = s == ServerStatus.Crashed || (C.LastError != null && !alive && C.BusyText == null) ? Theme.Red : Theme.Muted;

            // banner
            string banner = null;
            if (C.AppUpdate != null) banner = "CardboardBox " + C.AppUpdate.Tag + " is available (you have " + AppPaths.AppVersionText + ").";
            else if (C.RepoUpdatePending) banner = "New server files from GitHub are waiting - they're applied when the server restarts" + (C.Settings.AutoRestartForUpdates ? " (happening automatically)." : ".");
            else if (C.RestartNeeded && alive) banner = "Settings changed - restart the server to apply them.";
            _banner.Visible = banner != null;
            _bannerText.Text = banner ?? "";
            _appUpdate.Visible = C.AppUpdate != null;

            var m = C.Manifest;
            _announcement.Text = string.IsNullOrWhiteSpace(m.Announcement) ? "No message right now." : m.Announcement;

            var sha = C.State.AppliedSha;
            _github.Value.Text = string.IsNullOrEmpty(sha) ? "Not synced yet" : "\u2714 " + sha.Substring(0, Math.Min(7, sha.Length));

            _version.Value.Text = C.State.PaperVersion != null ? "Minecraft " + C.State.PaperVersion : "Not installed yet";
            _version.Detail.Text = C.State.PaperBuild > 0
                ? "Paper build #" + C.State.PaperBuild + " \u00b7 Java " + (C.State.JavaMinimum > 0 ? Installer.PickJavaFeature(C.State.JavaMinimum).ToString() : "?")
                : "Installs automatically.";

            _lan.Value.Text = LanAddress;
            _lan.Detail.Text = "For players on the same Wi-Fi / network.";
            _wan.Value.Text = WanAddress ?? "Looking up...";
            _wan.Detail.Text = "For friends elsewhere. Needs port " + C.Port + " forwarded on the router.";

            UpdateActivity();
            Tick();
        }

        public override void Tick()
        {
            var st = C.Stats;
            bool alive = C.Server.IsAlive;
            var players = C.Server.Players;

            _players.Value.Text = (alive ? players.Count.ToString() : "-") + " / " + C.MaxPlayers;
            _players.Detail.Text = !alive ? "Server is stopped." : players.Count == 0 ? "Nobody online yet." : string.Join(", ", players.Take(8)) + (players.Count > 8 ? "..." : "");

            _cpu.Value.Text = alive ? st.ServerCpu.ToString("0") + "%" : "-";
            _cpu.Detail.Text = "Whole PC: " + st.SystemCpu.ToString("0") + "% \u00b7 " + Environment.ProcessorCount + " CPU threads";
            _cpu.Chart.Values = st.ServerCpuHistory;
            _cpu.Chart.SecondValues = st.SystemCpuHistory;
            _cpu.Chart.Invalidate();

            double gb = 1024.0 * 1024 * 1024;
            _ram.Value.Text = alive ? (st.ServerRamBytes / gb).ToString("0.0") + " GB" : "-";
            _ram.Detail.Text = (C.RamMB / 1024.0).ToString("0.#") + " GB given to the server \u00b7 PC: " +
                               (st.SystemRamUsedBytes / gb).ToString("0.0") + " / " + (st.SystemRamTotalBytes / gb).ToString("0") + " GB used";
            _ram.Chart.Values = st.ServerRamHistory;
            _ram.Chart.Max = C.RamMB * 1.25;
            _ram.Chart.Invalidate();

            _uptime.Value.Text = alive ? Theme.Duration(DateTime.Now - C.Server.StartedAt) : "-";
            _uptime.Detail.Text = alive ? "Started at " + C.Server.StartedAt.ToString("HH:mm") : "Server is stopped.";

            var next = C.NextCheck == DateTime.MaxValue ? "" : " \u00b7 next check in " + Math.Max(0, (int)Math.Ceiling((C.NextCheck - DateTime.Now).TotalMinutes)) + " min";
            _github.Detail.Text = "Checked " + Theme.Ago(C.State.LastCheckUtc) + next +
                                  (C.State.LastCheckResult != null && C.State.LastCheckResult.StartsWith("Failed") ? "\n(last check failed)" : "");
        }
    }
}
