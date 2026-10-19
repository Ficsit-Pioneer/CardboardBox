using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CardboardBox.Core;
using CardboardBox.UI.Pages;

namespace CardboardBox.UI
{
    public class MainForm : Form
    {
        public readonly AppController Controller;
        public readonly ConsolePage ConsoleView;

        private readonly List<Page> _pages = new List<Page>();
        private readonly Dictionary<Page, Button> _nav = new Dictionary<Page, Button>();
        private readonly Panel _host;
        private readonly Label _statusDot, _statusText, _sideStatus, _sideRemote;
        private readonly ProgressBar _progress;
        private readonly Timer _timer;
        private Page _current;
        private bool _closingForReal;

        public MainForm(bool startServer = false)
        {
            Controller = new AppController { StartServerOnLaunch = startServer };

            Text = "CardboardBox";
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            BackColor = Theme.Bg;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Size = new Size(1180, 780);
            MinimumSize = new Size(980, 660);
            StartPosition = FormStartPosition.CenterScreen;

            // ---------- sidebar
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Theme.Sidebar };
            var logo = new PictureBox
            {
                Image = Theme.AppIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(40, 40), Location = new Point(16, 18), BackColor = Color.Transparent,
            };
            try { logo.Image = new Icon(Theme.AppIcon, 48, 48).ToBitmap(); } catch { }
            var name = new Label { Text = "CardboardBox", Font = new Font("Segoe UI Semibold", 13f), ForeColor = Color.White, AutoSize = true, Location = new Point(62, 18) };
            _sideStatus = new Label { Text = "Stopped", Font = Theme.Small, ForeColor = Theme.SidebarText, AutoSize = true, Location = new Point(64, 42) };
            sidebar.Controls.Add(logo);
            sidebar.Controls.Add(name);
            sidebar.Controls.Add(_sideStatus);

            var navPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Location = new Point(0, 80), Width = 210, Height = 420, BackColor = Theme.Sidebar,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            sidebar.Controls.Add(navPanel);

            var bottomLinks = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Bottom,
                Height = 116, Padding = new Padding(14, 6, 6, 6), BackColor = Theme.Sidebar,
            };
            _sideRemote = new Label { AutoSize = true, Font = Theme.Small, ForeColor = Theme.SidebarText, Margin = new Padding(0, 0, 0, 6), UseMnemonic = false, Visible = false };
            bottomLinks.Controls.Add(_sideRemote);
            bottomLinks.Controls.Add(SideLink("Open server folder", () => Theme.OpenFolder(AppPaths.Root)));
            bottomLinks.Controls.Add(SideLink("Open GitHub repo", () => Theme.OpenUrl(Controller.Settings.RepoUrl)));
            bottomLinks.Controls.Add(new Label { Text = "Version " + AppPaths.AppVersionText, ForeColor = Theme.Gray, Font = Theme.Small, AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
            sidebar.Controls.Add(bottomLinks);

            // ---------- status bar
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(0xEA, 0xE2, 0xD5), Padding = new Padding(12, 0, 12, 0) };
            _statusDot = new Label { Text = "\u25cf", ForeColor = Theme.Gray, AutoSize = true, Font = new Font("Segoe UI", 11f), Location = new Point(10, 4) };
            _statusText = new Label { Text = "Getting ready...", UseMnemonic = false, AutoSize = true, ForeColor = Theme.Text, Location = new Point(30, 7) };
            _progress = new ProgressBar { Width = 220, Height = 14, Anchor = AnchorStyles.Right | AnchorStyles.Top, Visible = false, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            statusBar.Controls.Add(_statusDot);
            statusBar.Controls.Add(_statusText);
            statusBar.Controls.Add(_progress);
            statusBar.Resize += (s, e) => _progress.Location = new Point(statusBar.Width - _progress.Width - 14, 8);

            _host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            Controls.Add(_host);
            Controls.Add(statusBar);
            Controls.Add(sidebar);

            // ---------- pages
            ConsoleView = new ConsolePage(this, Controller);
            var pages = new Page[]
            {
                new DashboardPage(this, Controller),
                ConsoleView,
                new SettingsPage(this, Controller),
                new PluginsPage(this, Controller),
                new PlayersPage(this, Controller),
                new BackupsPage(this, Controller),
                new GitHubPage(this, Controller),
            };
            string[] icons = { "\u2302", "\u276f", "\u2699", "\u2726", "\u263a", "\u25a3", "\u21c5" };
            for (int i = 0; i < pages.Length; i++)
            {
                var page = pages[i];
                page.Visible = false;
                _host.Controls.Add(page);
                _pages.Add(page);
                var btn = NavButton(icons[i] + "   " + page.Title);
                btn.Click += (s, e) => ShowPage(page);
                navPanel.Controls.Add(btn);
                _nav[page] = btn;
            }

            Controller.Changed += RefreshAll;
            Controller.ExitRequested += () => { _closingForReal = true; Close(); };
            Log.Message += (level, msg) => ConsoleView.AddAppLine(level, msg);
            Controller.Server.OutputLine += line => ConsoleView.AddServerLine(line);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                try
                {
                    Controller.Tick();
                    _current?.Tick();
                }
                catch (Exception ex) { Log.WriteFileOnly("tick failed: " + ex); }
            };

            ShowPage(_pages[0]);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
            RefreshAll();
            await Controller.InitAsync();
            RefreshAll();
        }

        public void ShowPage(Page page)
        {
            if (_current == page) return;
            _host.SuspendLayout();
            foreach (var p in _pages) p.Visible = p == page;
            _host.ResumeLayout();
            _current = page;
            foreach (var kv in _nav)
            {
                bool active = kv.Key == page;
                kv.Value.BackColor = active ? Theme.Accent : Theme.Sidebar;
                kv.Value.ForeColor = active ? Color.White : Theme.SidebarText;
                kv.Value.FlatAppearance.MouseOverBackColor = active ? Theme.Accent : Theme.SidebarHover;
            }
            page.RefreshView();
        }

        public T PageOf<T>() where T : Page => _pages.OfType<T>().FirstOrDefault();

        private void RefreshAll()
        {
            if (IsDisposed) return;
            var s = Controller.Server.Status;
            _statusDot.ForeColor = StatusColor(s);
            _sideStatus.Text = StatusText(s);
            _sideRemote.Visible = Controller.Remote.Available;
            _sideRemote.Text = "Owner's admin panel: " + (Controller.Settings.AllowRemoteAdmin ? Controller.Remote.StateText.ToLowerInvariant() : "off");
            _sideRemote.ForeColor = Controller.Remote.Connected ? Color.FromArgb(0x8F, 0xD8, 0x9A) : Theme.SidebarText;

            if (Controller.BusyText != null)
            {
                _statusText.Text = Controller.BusyText;
                _progress.Visible = true;
                if (Controller.BusyPercent >= 0)
                {
                    _progress.Style = ProgressBarStyle.Continuous;
                    _progress.Value = Math.Max(0, Math.Min(100, Controller.BusyPercent));
                }
                else _progress.Style = ProgressBarStyle.Marquee;
            }
            else
            {
                _progress.Visible = false;
                _statusText.Text = Controller.LastError != null && !Controller.Server.IsAlive
                    ? "Problem: " + Controller.LastError
                    : StatusText(s) + (Controller.Server.IsAlive ? " - " + Controller.Server.Players.Count + " player(s) online" : "");
            }

            try { _current?.RefreshView(); } catch (Exception ex) { Log.WriteFileOnly("page refresh failed: " + ex); }
        }

        public static string StatusText(ServerStatus s)
        {
            switch (s)
            {
                case ServerStatus.Preparing: return "Getting ready...";
                case ServerStatus.Starting: return "Starting...";
                case ServerStatus.Running: return "Running";
                case ServerStatus.Stopping: return "Stopping...";
                case ServerStatus.Crashed: return "Crashed";
                default: return "Stopped";
            }
        }

        public static Color StatusColor(ServerStatus s)
        {
            switch (s)
            {
                case ServerStatus.Running: return Theme.Green;
                case ServerStatus.Preparing:
                case ServerStatus.Starting:
                case ServerStatus.Stopping: return Theme.Amber;
                case ServerStatus.Crashed: return Theme.Red;
                default: return Theme.Gray;
            }
        }

        // ------------------------------------------------------------------ actions shared by pages

        /// <summary>Start button: asks for the Minecraft EULA the first time.</summary>
        public async Task StartServerAsync()
        {
            if (!Controller.Settings.EulaAccepted)
            {
                using (var dlg = new EulaDialog())
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                }
                Controller.Settings.EulaAccepted = true;
                Controller.Settings.Save();
                Log.Info("Minecraft EULA accepted.");
            }
            if (!await Controller.StartAsync() && Controller.LastError != null)
                MessageBox.Show(this, Controller.LastError, "The server couldn't start", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (_closingForReal || !Controller.Server.IsAlive)
            {
                base.OnFormClosing(e);
                return;
            }
            e.Cancel = true;
            var answer = MessageBox.Show(this,
                "The Minecraft server is still running.\n\nStop it (saving the world) and close CardboardBox?",
                "Close CardboardBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            Enabled = false;
            await Controller.StopAsync();
            _closingForReal = true;
            Close();
        }

        // ------------------------------------------------------------------ sidebar helpers

        private static Button NavButton(string text)
        {
            var b = new Button
            {
                Text = text, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat,
                ForeColor = Theme.SidebarText, BackColor = Theme.Sidebar, Font = new Font("Segoe UI Semibold", 10.5f),
                Width = 210, Height = 42, Margin = new Padding(0), Padding = new Padding(18, 0, 0, 0), Cursor = Cursors.Hand, UseMnemonic = false,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.SidebarHover;
            return b;
        }

        private static LinkLabel SideLink(string text, Action click)
        {
            var l = Theme.Link(text, click);
            l.LinkColor = Theme.SidebarText;
            l.VisitedLinkColor = Theme.SidebarText;
            l.ActiveLinkColor = Color.White;
            l.Margin = new Padding(0, 2, 0, 2);
            return l;
        }
    }
}
