using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>MOTD, icon, max players, RAM and the other everyday server settings.</summary>
    public partial class SettingsPage : Page
    {
        private readonly TextBox _motd;
        private readonly MotdPreview _preview;
        private readonly PictureBox _icon;
        private readonly NumericUpDown _maxPlayers, _port, _viewDistance;
        private readonly TrackBar _ram;
        private readonly Label _ramLabel, _overridesNote;
        private readonly ComboBox _difficulty, _gamemode;
        private readonly CheckBox _whitelist, _flight, _autoStart, _crashRestart, _updateRestart, _backupFirst, _awake;
        private readonly Button _save, _push, _reset;
        private readonly Label _playitStatus;
        private readonly CheckBox _playitOn;
        private readonly Button _playitSetup, _playitForget;
        private bool _refreshingPlayit;
        private string _pendingIcon;
        private bool _loading;

        private static readonly string[] Difficulties = { "peaceful", "easy", "normal", "hard" };
        private static readonly string[] Gamemodes = { "survival", "creative", "adventure", "spectator" };

        public SettingsPage(MainForm main, AppController c)
            : base(main, c, "Server Settings", "Changes are saved on this PC. Upload them to GitHub to share them with every copy of CardboardBox.")
        {
            BuildServerTypeCard();

            // ---------- look
            var look = new Card("How your server looks");
            _motd = new TextBox { Multiline = true, Width = 460, Height = 46, Font = Theme.Mono, ScrollBars = ScrollBars.None, AcceptsReturn = true };
            _motd.TextChanged += (s, e) => { if (_motd.Lines.Length > 2) _motd.Text = string.Join("\r\n", _motd.Lines, 0, 2); UpdatePreview(); };
            _preview = new MotdPreview { Margin = new Padding(0, 6, 0, 4) };
            _icon = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(0x24, 0x22, 0x20), Margin = new Padding(0, 0, 12, 0) };
            var chooseIcon = Theme.Secondary("Choose picture...");
            chooseIcon.Click += (s, e) => ChooseIcon();
            var resetIcon = Theme.Secondary("Use GitHub icon");
            resetIcon.Click += (s, e) => { _pendingIcon = ""; LoadIconPreview(Path.Combine(RepoSync.RepoServerFolder, "server-icon.png")); };
            _maxPlayers = new NumericUpDown { Minimum = 1, Maximum = 1000, Width = 90 };
            _maxPlayers.ValueChanged += (s, e) => UpdatePreview();

            look.Add(
                FieldLabel("Server message (MOTD)"),
                Hint("Shown in the multiplayer server list. Two lines max. Colors: &a green, &c red, &e yellow, &b aqua, &6 gold, &l bold, &r reset."),
                _motd, _preview,
                FieldLabel("Server icon"),
                Hint("Any picture works - CardboardBox shrinks it to 64\u00d764 for you."),
                Theme.Row(_icon, Theme.Stack(chooseIcon, resetIcon)),
                FieldLabel("Max players"), _maxPlayers);
            Place(look);

            // ---------- how it runs
            var run = new Card("How it runs");
            _ram = new TrackBar { Minimum = 2, Maximum = Math.Max(2, AppController.MaxSafeRamMB / 512), TickFrequency = 2, SmallChange = 1, LargeChange = 2, Width = 380 };
            _ramLabel = new Label { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small };
            _ram.ValueChanged += (s, e) => UpdateRamLabel();
            _port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Width = 90 };
            _difficulty = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            _difficulty.Items.AddRange(new object[] { "Peaceful", "Easy", "Normal", "Hard" });
            _gamemode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            _gamemode.Items.AddRange(new object[] { "Survival", "Creative", "Adventure", "Spectator" });
            _viewDistance = new NumericUpDown { Minimum = 3, Maximum = 32, Width = 90 };
            _whitelist = new CheckBox { Text = "Only let whitelisted players join (see the Players page)", AutoSize = true };
            _flight = new CheckBox { Text = "Allow flying (stops players being kicked by some plugins)", AutoSize = true };

            run.Add(
                FieldLabel("RAM for the server"), _ram, _ramLabel,
                Theme.Row(Theme.Stack(FieldLabel("Difficulty"), _difficulty), Theme.Stack(FieldLabel("Game mode"), _gamemode)),
                Theme.Row(Theme.Stack(FieldLabel("Port"), _port), Theme.Stack(FieldLabel("View distance (chunks)"), _viewDistance)),
                _whitelist, _flight);
            Place(run);

            BuildWorldCard();

            // ---------- playit.gg
            var share = new Card("Friends outside your home (playit.gg)");
            _playitStatus = new Label { AutoSize = true, MaximumSize = new Size(900, 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, 6) };
            _playitOn = new CheckBox { Text = "Run the playit.gg tunnel whenever the server is running", AutoSize = true };
            _playitOn.CheckedChanged += (s, e) => { if (!_refreshingPlayit) C.SetPlayitEnabled(_playitOn.Checked); };
            _playitSetup = Theme.Primary("Set up playit.gg...");
            _playitSetup.Click += (s, e) => { using (var dlg = new PlayitDialog(C)) dlg.ShowDialog(this); RefreshView(); };
            _playitForget = Theme.Secondary("Disconnect");
            _playitForget.Click += (s, e) =>
            {
                if (Confirm("Disconnect CardboardBox from playit.gg on this PC?\n\nFriends won't be able to use the playit.gg address until you set it up again.")) C.ForgetPlayit();
                RefreshView();
            };
            share.Add(
                Hint("playit.gg is a free service that gives your server an address friends can join from anywhere, with no router setup. " +
                     "You'll need a free playit.gg account (one click to approve CardboardBox)."),
                _playitStatus, _playitOn,
                Theme.Row(_playitSetup, _playitForget, Theme.Link("Open my playit.gg dashboard", () => Theme.OpenUrl(Playit.DashboardUrl))));
            Place(share, 2);

            BuildRemoteCard();

            // ---------- automatic behaviour
            var auto = new Card("CardboardBox behaviour");
            _autoStart = new CheckBox { Text = "Start the server when CardboardBox opens", AutoSize = true };
            _crashRestart = new CheckBox { Text = "Restart the server automatically if it crashes", AutoSize = true };
            _updateRestart = new CheckBox { Text = "Restart automatically when GitHub has updates (players get a 60-second warning)", AutoSize = true };
            _backupFirst = new CheckBox { Text = "Back up the world before applying updates", AutoSize = true };
            _awake = new CheckBox { Text = "Keep the PC awake while the server is running", AutoSize = true };
            auto.Add(_autoStart, _crashRestart, _updateRestart, _backupFirst, _awake,
                Hint("CardboardBox checks GitHub for new server files every " + C.Settings.CheckIntervalMinutes + " minutes (change this on the GitHub page)."));
            Place(auto, 2);

            // ---------- buttons
            _save = Theme.Primary("Save");
            _save.Click += (s, e) => Run(async () => { Save(); await System.Threading.Tasks.Task.CompletedTask; }, _save);
            _push = Theme.Secondary("Save && upload to GitHub");
            _push.Click += (s, e) => Run(async () => { Save(); await C.PushSettingsToGitHubAsync(); LoadValues(); }, _push);
            _reset = Theme.Secondary("Reset to GitHub values");
            _reset.Click += (s, e) => { if (Confirm("Undo every change made on this PC and use the settings from GitHub?")) { C.ResetOverrides(); _pendingIcon = null; LoadValues(); } };
            _overridesNote = Hint("");
            var buttons = Theme.Stack(Theme.Row(_save, _push, _reset), _overridesNote);
            Place(buttons, 2);

            VisibleChanged += (s, e) => { if (Visible) { LoadValues(); LoadServerType(); } };
        }

        private void LoadValues()
        {
            _loading = true;
            try
            {
                string P(string key, string def) => ServerProperties.Effective(C.Settings, key, def);
                _motd.Text = (P("motd", "A Minecraft Server") ?? "").Replace('\u00a7', '&').Replace("\r", "").Replace("\n", "\r\n");
                _maxPlayers.Value = Clamp(_maxPlayers, P("max-players", "20"));
                _port.Value = Clamp(_port, P("server-port", "25565"));
                _viewDistance.Value = Clamp(_viewDistance, P("view-distance", "10"));
                _difficulty.SelectedIndex = Math.Max(0, Array.IndexOf(Difficulties, (P("difficulty", "easy") ?? "easy").ToLowerInvariant()));
                _gamemode.SelectedIndex = Math.Max(0, Array.IndexOf(Gamemodes, (P("gamemode", "survival") ?? "survival").ToLowerInvariant()));
                _whitelist.Checked = string.Equals(P("white-list", "false"), "true", StringComparison.OrdinalIgnoreCase);
                _flight.Checked = string.Equals(P("allow-flight", "false"), "true", StringComparison.OrdinalIgnoreCase);
                LoadWorldValues(P);

                _ram.Value = Math.Max(_ram.Minimum, Math.Min(_ram.Maximum, C.RamMB / 512));
                _autoStart.Checked = C.Settings.AutoStartServer;
                _crashRestart.Checked = C.Settings.AutoRestartOnCrash;
                _updateRestart.Checked = C.Settings.AutoRestartForUpdates;
                _backupFirst.Checked = C.Settings.BackupBeforeUpdates;
                _awake.Checked = C.Settings.KeepPcAwake;

                _pendingIcon = null;
                LoadIconPreview(File.Exists(AppPaths.OverrideIcon) ? AppPaths.OverrideIcon : Path.Combine(AppPaths.Server, "server-icon.png"));
                UpdateRamLabel();
                RefreshView();
            }
            finally { _loading = false; }
            UpdatePreview();
        }

        private static decimal Clamp(NumericUpDown n, string value)
        {
            decimal.TryParse(value, out var v);
            return Math.Max(n.Minimum, Math.Min(n.Maximum, v == 0 ? n.Minimum : v));
        }

        private void UpdateRamLabel()
        {
            int mb = _ram.Value * 512;
            int recommended = Math.Min(C.Manifest.DefaultRamGB * 1024, AppController.MaxSafeRamMB);
            _ramLabel.Text = (mb / 1024.0).ToString("0.0") + " GB  \u00b7  this PC has " + (AppController.SystemRamMB / 1024.0).ToString("0") +
                             " GB  \u00b7  recommended " + (recommended / 1024.0).ToString("0.#") + " GB";
            _ramLabel.ForeColor = mb > AppController.SystemRamMB * 0.75 ? Theme.Red : Theme.Muted;
        }

        private void UpdatePreview()
        {
            if (_loading) return;
            _preview.ServerName = string.IsNullOrWhiteSpace(C.Manifest.ServerName) ? "Minecraft Server" : C.Manifest.ServerName;
            _preview.Motd = _motd.Text.Replace("\r\n", "\n");
            _preview.Icon = _icon.Image;
            _preview.PlayersText = (C.Server.IsAlive ? C.Server.Players.Count : 0) + "/" + _maxPlayers.Value;
            _preview.Invalidate();
        }

        private void LoadIconPreview(string path)
        {
            try
            {
                if (File.Exists(path))
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        _icon.Image = new Bitmap(Image.FromStream(fs));
                else _icon.Image = null;
            }
            catch { _icon.Image = null; }
            UpdatePreview();
        }

        private void ChooseIcon()
        {
            using (var dlg = new OpenFileDialog { Title = "Choose a server icon", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var src = Image.FromFile(dlg.FileName))
                    using (var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            // crop to a centered square, then scale
                            int side = Math.Min(src.Width, src.Height);
                            var crop = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                            g.DrawImage(src, new Rectangle(0, 0, 64, 64), crop, GraphicsUnit.Pixel);
                        }
                        var tmp = Path.Combine(AppPaths.Cache, "new-icon.png");
                        bmp.Save(tmp, ImageFormat.Png);
                        _pendingIcon = tmp;
                        LoadIconPreview(tmp);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "That picture couldn't be opened:\n" + ex.Message, "CardboardBox");
                }
            }
        }

        private void Save()
        {
            var motd = Regex.Replace(_motd.Text.Replace("\r\n", "\n").TrimEnd('\n'), "&([0-9a-fk-orA-FK-OR])", "\u00a7$1");
            var changes = new Dictionary<string, string>
            {
                ["motd"] = motd,
                ["max-players"] = ((int)_maxPlayers.Value).ToString(),
                ["server-port"] = ((int)_port.Value).ToString(),
                ["view-distance"] = ((int)_viewDistance.Value).ToString(),
                ["difficulty"] = Difficulties[Math.Max(0, _difficulty.SelectedIndex)],
                ["gamemode"] = Gamemodes[Math.Max(0, _gamemode.SelectedIndex)],
                ["white-list"] = _whitelist.Checked ? "true" : "false",
                ["allow-flight"] = _flight.Checked ? "true" : "false",
            };
            AddWorldChanges(changes);
            C.SaveServerProperties(changes);

            if (_pendingIcon == "") C.SetServerIcon(null);
            else if (_pendingIcon != null) C.SetServerIcon(_pendingIcon);
            _pendingIcon = null;

            int ramMB = _ram.Value * 512;
            C.Settings.RamMB = ramMB == Math.Min(C.Manifest.DefaultRamGB * 1024, AppController.MaxSafeRamMB) ? 0 : ramMB;
            C.Settings.AutoStartServer = _autoStart.Checked;
            C.Settings.AutoRestartOnCrash = _crashRestart.Checked;
            C.Settings.AutoRestartForUpdates = _updateRestart.Checked;
            C.Settings.BackupBeforeUpdates = _backupFirst.Checked;
            C.Settings.KeepPcAwake = _awake.Checked;
            C.Settings.Save();
            if (C.Server.IsAlive) C.RestartNeeded = true;

            Log.Success("Server settings saved" + (C.Server.IsAlive ? " - restart the server to apply them." : "."));
            RefreshView();
        }

        public override void RefreshView()
        {
            RefreshServerType();
            RefreshRemote();
            _refreshingPlayit = true;
            try
            {
                bool linked = C.Settings.HasPlayit;
                _playitOn.Enabled = linked;
                _playitOn.Checked = C.UsingPlayit;
                _playitForget.Visible = linked;
                _playitSetup.Text = linked ? "Set up again..." : "Set up playit.gg...";
                _playitStatus.Text = !linked ? "Not set up yet."
                    : "\u2714 Linked to playit.gg." + (string.IsNullOrEmpty(C.Settings.PlayitAddress) ? " The address appears once the server runs."
                        : " Friends can join at: " + C.Settings.PlayitAddress) + (C.Settings.PlayitEnabled ? "" : " (tunnel switched off)");
                _playitStatus.ForeColor = linked ? Theme.Green : Theme.Muted;
            }
            finally { _refreshingPlayit = false; }

            _push.Enabled = C.Settings.HasToken;
            int n = C.Settings.PropertyOverrides.Count + (File.Exists(AppPaths.OverrideIcon) ? 1 : 0);
            _overridesNote.Text = (n == 0 ? "All settings match GitHub." : n + " setting(s) on this PC differ from GitHub.") +
                                  (C.Settings.HasToken ? "" : "  To upload changes, add a GitHub token on the GitHub page.");
        }
    }
}
