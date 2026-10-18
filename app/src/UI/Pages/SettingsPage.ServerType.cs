using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>The "Server type" card (software, Minecraft version, crossplay) and the "World" card.</summary>
    public partial class SettingsPage
    {
        private ComboBox _software, _version, _levelType;
        private CheckBox _crossplay, _hardcore;
        private Label _typeHint, _typeInstalled, _worldInfo;
        private Button _applyType;
        private TextBox _seed;
        private bool _loadingType;
        private int _versionRequest;

        private class SoftwareItem
        {
            public readonly ServerSoftware Software;
            public SoftwareItem(ServerSoftware s) { Software = s; }
            public override string ToString() => Software.Blurb;
        }

        private class VersionItem
        {
            public readonly string Value, Text;
            public VersionItem(string value, string text) { Value = value; Text = text; }
            public override string ToString() => Text;
        }

        private static readonly string[] LevelTypes = { "minecraft:normal", "minecraft:flat", "minecraft:large_biomes", "minecraft:amplified" };

        // ------------------------------------------------------------------ server type

        private void BuildServerTypeCard()
        {
            var card = new Card("Server type");
            _software = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
            foreach (var sw in ServerSoftware.All) _software.Items.Add(new SoftwareItem(sw));
            _version = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            _crossplay = new CheckBox { Text = "Let Bedrock players join too (phones, tablets, consoles and Windows) - free, using Geyser", AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
            _typeHint = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false, Margin = new Padding(0, 2, 0, 8) };
            _typeInstalled = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false, Margin = new Padding(0, 2, 0, 0) };
            _applyType = Theme.Primary("Apply server type");

            _software.SelectedIndexChanged += async (s, e) =>
            {
                if (_loadingType) return;
                await LoadVersionsAsync(SelectedSoftware, SelectedVersion ?? "latest");
                UpdateTypeHint();
            };
            _version.SelectedIndexChanged += (s, e) => { if (!_loadingType) UpdateTypeHint(); };
            _crossplay.CheckedChanged += (s, e) => { if (!_loadingType) UpdateTypeHint(); };
            _applyType.Click += (s, e) => ApplyServerType();

            card.Add(
                Theme.Row(Theme.Stack(FieldLabel("Software"), _software), Theme.Stack(FieldLabel("Minecraft version"), _version)),
                _crossplay, _typeHint, Theme.Row(_applyType), _typeInstalled);
            Place(card, 2);
        }

        private ServerSoftware SelectedSoftware => (_software.SelectedItem as SoftwareItem)?.Software;
        private string SelectedVersion => (_version.SelectedItem as VersionItem)?.Value;

        private async void LoadServerType()
        {
            _loadingType = true;
            try
            {
                var current = C.Software;
                _software.SelectedItem = _software.Items.Cast<SoftwareItem>().First(i => i.Software.Id == current.Id);
                _crossplay.Checked = C.EffectiveCrossplay;
            }
            finally { _loadingType = false; }
            await LoadVersionsAsync(C.Software, C.EffectiveVersion);
            UpdateTypeHint();
        }

        /// <summary>Fills the version list for a software (from its own website), keeping the chosen version selected.</summary>
        private async System.Threading.Tasks.Task LoadVersionsAsync(ServerSoftware sw, string select)
        {
            if (sw == null) return;
            int request = ++_versionRequest;
            _loadingType = true;
            _version.Items.Clear();
            _version.Items.Add(new VersionItem(select, "Loading..."));
            _version.SelectedIndex = 0;
            _version.Enabled = false;
            _loadingType = false;

            List<string> versions = null;
            string error = null;
            try { versions = await sw.GetVersionsAsync(); }
            catch (Exception ex) { error = Errors.Friendly(ex); }
            if (request != _versionRequest || IsDisposed) return;

            _loadingType = true;
            try
            {
                _version.Items.Clear();
                if (versions != null && versions.Count > 0)
                {
                    _version.Items.Add(new VersionItem("latest", "Latest (" + versions[0] + ")"));
                    foreach (var v in versions.Take(80)) _version.Items.Add(new VersionItem(v, v));
                }
                else
                {
                    _version.Items.Add(new VersionItem("latest", "Latest"));
                    if (error != null) Log.Warn("Couldn't load the " + sw.Name + " version list: " + error);
                }
                if (!string.IsNullOrEmpty(select) && !_version.Items.Cast<VersionItem>().Any(i => i.Value == select))
                    _version.Items.Insert(1, new VersionItem(select, select));
                _version.SelectedItem = _version.Items.Cast<VersionItem>().FirstOrDefault(i => string.Equals(i.Value, select, StringComparison.OrdinalIgnoreCase))
                                        ?? _version.Items[0];
                _version.Enabled = true;
            }
            finally { _loadingType = false; }
        }

        private void UpdateTypeHint()
        {
            var sw = SelectedSoftware;
            if (sw == null) return;

            _loadingType = true;
            _crossplay.Enabled = sw.SupportsCrossplay;
            if (!sw.SupportsCrossplay) _crossplay.Checked = false;
            _loadingType = false;

            var lines = new List<string>();
            switch (sw.Id)
            {
                case "paper": lines.Add("Uses plugins (see the Plugins & Mods page). Players join with the normal game - nothing to install."); break;
                case "purpur": lines.Add("Uses the same plugins as Paper, plus extra settings in purpur.yml. Players join with the normal game."); break;
                case "fabric": lines.Add("Uses Fabric mods. Fabric API is added for you. Server-side mods work with the normal game; others need the same mods on each player's PC."); break;
                case "neoforge":
                case "forge": lines.Add("Uses " + sw.Name + " mods. Most big mods must also be installed in each player's game (with " + sw.Name + ")."); break;
                case "vanilla": lines.Add("Plain Minecraft. No plugins or mods."); break;
            }
            if (!sw.SupportsCrossplay) lines.Add("Crossplay (Bedrock players) needs Paper, Purpur, Fabric or NeoForge.");
            else if (_crossplay.Checked)
                lines.Add("Bedrock players add a server with your address and port " + GeyserConfig.BedrockPort + ". " +
                          (sw.GeyserPlatform == "spigot" ? "Geyser, Floodgate (and ViaVersion if needed) are installed for you."
                                                         : "Geyser and Floodgate mods are installed for you."));

            if (C.ChangesWorldFamily(sw.Id))
                lines.Add(ServerSoftware.FamilyName(sw.WorldFamily) + " can't open a " + ServerSoftware.FamilyName(C.InstalledSoftware.WorldFamily) +
                          " world, so each keeps its own world. Your current world is put aside and comes back if you switch back.");
            var target = SelectedVersion == "latest" ? (_version.Items.Count > 1 ? (_version.Items[1] as VersionItem)?.Value : null) : SelectedVersion;
            var worldVersion = C.WorldVersionFor(sw.Id);
            bool blocked = target != null && !string.IsNullOrEmpty(worldVersion) && VersionOrder.Compare(target, worldVersion) < 0;
            if (blocked)
                lines.Add("That world was played on Minecraft " + worldVersion + " and can't go back to " + target +
                          ". Pick " + worldVersion + " or newer, or start a new world (below) first.");
            _typeHint.Text = string.Join("\n", lines);
            _typeHint.ForeColor = blocked ? Theme.Red : Theme.Muted;

            bool changed = sw.Id != C.Software.Id ||
                           !string.Equals(SelectedVersion, C.EffectiveVersion, StringComparison.OrdinalIgnoreCase) ||
                           _crossplay.Checked != C.CrossplayActive;
            _applyType.Enabled = changed && !blocked && SelectedVersion != null && _version.Enabled;
        }

        private void RefreshServerType()
        {
            if (_typeInstalled == null) return;
            var installed = C.DescribeInstalled();
            _typeInstalled.Text = "Installed now: " + installed +
                (C.Settings.HasServerTypeOverride ? "   \u2022   chosen on this PC (differs from GitHub)" : "   \u2022   as set on GitHub");
            _worldInfo.Text = Backups.WorldFolders().Count == 0 ? "There's no world yet - one is created on the next start."
                : "The current world was last played on Minecraft " + (C.State.WorldVersion ?? "?") + ".";
        }

        private void ApplyServerType()
        {
            var sw = SelectedSoftware;
            var version = SelectedVersion;
            if (sw == null || version == null) return;
            bool crossplay = _crossplay.Checked && sw.SupportsCrossplay;

            var current = C.Software;
            var text = "Switch the server to " + sw.Name + " " + (version == "latest" ? "(latest Minecraft)" : version) +
                       (crossplay ? " with Bedrock crossplay" : "") + "?\n\n" +
                       "CardboardBox backs up the world, downloads what's needed and " + (C.Server.IsAlive ? "restarts" : "prepares") + " the server.";
            if (C.ChangesWorldFamily(sw.Id))
                text += "\n\nYour current world is put aside (it comes back if you switch back), because " +
                        sw.Name + " can't open it.";
            if (current.AddonFolder != sw.AddonFolder && current.AddonFolder != null)
                text += "\n\nNote: your " + current.AddonWord + " won't load on " + sw.Name +
                        (sw.AddonFolder == null ? " (it doesn't support add-ons)." : " (it uses " + sw.AddonWord + " instead). They're kept in case you switch back.");
            if (version != "latest" && VersionOrder.Compare(version, C.State.InstalledMcVersion ?? version) > 0)
                text += "\n\nUpgrading the world to a newer Minecraft can't be undone (the backup can bring it back).";
            text += "\n\nPlayers need the same Minecraft version in their game.";
            if (!Confirm(text, "Change server type")) return;

            Run(async () =>
            {
                await C.ChangeServerTypeAsync(sw.Id, version, crossplay);
                LoadServerType();
            }, _applyType);
        }

        // ------------------------------------------------------------------ world

        private void BuildWorldCard()
        {
            var card = new Card("World");
            _seed = new TextBox { Width = 260 };
            _levelType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            _levelType.Items.AddRange(new object[] { "Normal", "Flat", "Large biomes", "Amplified" });
            _hardcore = new CheckBox { Text = "Hardcore (one life - if you die, you can only watch)", AutoSize = true };
            _worldInfo = Hint("");
            var newWorld = Theme.Secondary("Start a new world...");
            newWorld.Click += (s, e) =>
            {
                if (C.Server.IsAlive) { MessageBox.Show(this, "Stop the server first, then try again.", "CardboardBox"); return; }
                if (!Confirm("Start a brand-new world?\n\nThe current world is moved into the backups folder (not deleted), " +
                             "and a new one is made with the seed and world type above when the server starts.", "New world")) return;
                try { Save(); C.StartNewWorld(); }
                catch (Exception ex) { MessageBox.Show(this, Errors.Friendly(ex), "CardboardBox"); }
                RefreshView();
            };
            card.Add(
                Hint("Seed, world type and hardcore are used when a new world is created."),
                Theme.Row(Theme.Stack(FieldLabel("Seed (empty = random)"), _seed), Theme.Stack(FieldLabel("World type"), _levelType)),
                _hardcore, _worldInfo, Theme.Row(newWorld));
            Place(card, 2);
        }

        private void LoadWorldValues(Func<string, string, string> p)
        {
            _seed.Text = p("level-seed", "") ?? "";
            var type = (p("level-type", "minecraft:normal") ?? "").ToLowerInvariant();
            if (!type.Contains(":")) type = "minecraft:" + (type == "default" ? "normal" : type);
            _levelType.SelectedIndex = Math.Max(0, Array.IndexOf(LevelTypes, type));
            _hardcore.Checked = string.Equals(p("hardcore", "false"), "true", StringComparison.OrdinalIgnoreCase);
        }

        private void AddWorldChanges(IDictionary<string, string> changes)
        {
            changes["level-seed"] = _seed.Text.Trim();
            changes["level-type"] = LevelTypes[Math.Max(0, _levelType.SelectedIndex)];
            changes["hardcore"] = _hardcore.Checked ? "true" : "false";
        }
    }
}
