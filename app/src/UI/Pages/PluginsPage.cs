using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>Lists plugins, lets you add / turn off / remove them, and upload them to GitHub.</summary>
    public class PluginsPage : Page
    {
        private readonly ListView _list;
        private readonly Button _add, _toggle, _remove, _upload;

        private class PluginRow
        {
            public string FileName;   // without .disabled
            public string FullPath;
            public bool Enabled;
            public string Name, Version, Source;
            public long Size;
        }

        public PluginsPage(MainForm main, AppController c)
            : base(main, c, "Plugins", "Plugins add features to the server. Changes take effect the next time the server starts.", 1)
        {
            var card = new Card(null);
            _list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                Width = 860, Height = 340, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Base,
            };
            _list.Columns.Add("Plugin", 200);
            _list.Columns.Add("Version", 110);
            _list.Columns.Add("Status", 80);
            _list.Columns.Add("Comes from", 230);
            _list.Columns.Add("File", 220);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += (s, e) => Toggle();

            _add = Theme.Primary("Add plugin (.jar)...");
            _add.Click += (s, e) => AddPlugin();
            _toggle = Theme.Secondary("Turn off");
            _toggle.Click += (s, e) => Toggle();
            _remove = Theme.Secondary("Remove");
            _remove.Click += (s, e) => Remove();
            _upload = Theme.Secondary("Upload to GitHub");
            _upload.Click += (s, e) => Upload();
            var folder = Theme.Secondary("Open plugins folder");
            folder.Click += (s, e) => Theme.OpenFolder(AppPaths.Plugins);
            var find = Theme.Secondary("Find plugins online");
            find.Click += (s, e) => Theme.OpenUrl("https://modrinth.com/plugins?l=paper");
            var refresh = Theme.Secondary("Refresh");
            refresh.Click += (s, e) => RefreshView();

            card.Add(_list, Theme.Row(_add, _toggle, _remove, _upload, folder, find, refresh));
            Place(card);

            var help = new Card("Where plugins come from");
            help.Add(
                Hint("• GitHub repo: .jar files in the repo's server/plugins folder. Every copy of CardboardBox gets them automatically."),
                Hint("• cardboardbox.json: plugins listed by name (from Modrinth) or by download link. CardboardBox downloads the version that matches your Minecraft version."),
                Hint("• Added on this PC: plugins you added with the button above. Only this PC has them, unless you upload them to GitHub."),
                Hint("Turning a plugin off keeps it switched off even when GitHub sends updates."));
            Place(help);

            VisibleChanged += (s, e) => { if (Visible) RefreshView(); };
            Resize += (s, e) => _list.Width = Math.Max(500, ClientSize.Width - Padding.Horizontal - 60);
        }

        private IEnumerable<PluginRow> Scan()
        {
            if (!Directory.Exists(AppPaths.Plugins)) yield break;
            var managed = new HashSet<string>(C.State.ManagedPlugins.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(AppPaths.Plugins))
            {
                bool enabled = path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                bool disabled = path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
                if (!enabled && !disabled) continue;
                var fileName = Path.GetFileName(enabled ? path : path.Substring(0, path.Length - ".disabled".Length));
                var row = new PluginRow { FileName = fileName, FullPath = path, Enabled = enabled, Size = new FileInfo(path).Length };
                ReadPluginYml(path, row);
                if (C.State.SyncedFiles.ContainsKey("plugins\\" + fileName)) row.Source = "GitHub repo";
                else if (managed.Contains(fileName)) row.Source = "cardboardbox.json (auto-download)";
                else row.Source = "Added on this PC";
                yield return row;
            }
        }

        /// <summary>Reads name + version from the plugin's plugin.yml / paper-plugin.yml.</summary>
        private static void ReadPluginYml(string jar, PluginRow row)
        {
            row.Name = Path.GetFileNameWithoutExtension(row.FileName);
            try
            {
                using (var fs = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry("paper-plugin.yml") ?? zip.GetEntry("plugin.yml");
                    if (entry == null) return;
                    using (var r = new StreamReader(entry.Open()))
                    {
                        var text = r.ReadToEnd();
                        var name = Regex.Match(text, @"^name:\s*['""]?([^'""\r\n]+)", RegexOptions.Multiline);
                        var version = Regex.Match(text, @"^version:\s*['""]?([^'""\r\n]+)", RegexOptions.Multiline);
                        if (name.Success) row.Name = name.Groups[1].Value.Trim();
                        if (version.Success) row.Version = version.Groups[1].Value.Trim();
                    }
                }
            }
            catch { /* not a valid jar - still list it */ }
        }

        public override void RefreshView()
        {
            if (!Visible) return;
            var selected = Selected?.FileName;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in Scan().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ListViewItem(new[] { p.Name, p.Version ?? "", p.Enabled ? "On" : "Off", p.Source, p.FileName }) { Tag = p };
                if (!p.Enabled) item.ForeColor = Theme.Gray;
                _list.Items.Add(item);
                if (p.FileName == selected) item.Selected = true;
            }
            if (_list.Items.Count == 0)
                _list.Items.Add(new ListViewItem(new[] { "No plugins yet", "", "", "Add one with the button below", "" }) { ForeColor = Theme.Gray });
            _list.EndUpdate();
            UpdateButtons();
        }

        private PluginRow Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as PluginRow : null;

        private void UpdateButtons()
        {
            var p = Selected;
            _toggle.Enabled = p != null;
            _toggle.Text = p != null && !p.Enabled ? "Turn on" : "Turn off";
            _remove.Enabled = p != null && p.Source == "Added on this PC";
            _upload.Enabled = p != null && C.Settings.HasToken && p.Source == "Added on this PC";
        }

        private void AddPlugin()
        {
            using (var dlg = new OpenFileDialog { Title = "Choose a plugin", Filter = "Minecraft plugins (*.jar)|*.jar", Multiselect = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        var dest = Path.Combine(AppPaths.Plugins, Path.GetFileName(file));
                        if (File.Exists(dest) && !Confirm(Path.GetFileName(file) + " already exists. Replace it?")) continue;
                        File.Copy(file, dest, true);
                        C.Settings.DisabledPlugins.Remove(Path.GetFileName(file));
                        Log.Success("Added plugin " + Path.GetFileName(file) + ".");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Couldn't add " + Path.GetFileName(file) + ":\n" + Errors.Friendly(ex) +
                                              (C.Server.IsAlive ? "\n\nTip: stop the server first." : ""), "CardboardBox");
                    }
                }
                C.Settings.Save();
                MarkRestart();
                RefreshView();
            }
        }

        private void Toggle()
        {
            var p = Selected;
            if (p == null) return;
            try
            {
                var on = Path.Combine(AppPaths.Plugins, p.FileName);
                var off = on + ".disabled";
                if (p.Enabled)
                {
                    if (File.Exists(off)) File.Delete(off);
                    File.Move(on, off);
                    if (!C.Settings.DisabledPlugins.Contains(p.FileName)) C.Settings.DisabledPlugins.Add(p.FileName);
                    Log.Info("Turned off plugin " + p.Name + ".");
                }
                else
                {
                    if (File.Exists(on)) File.Delete(on);
                    File.Move(off, on);
                    C.Settings.DisabledPlugins.RemoveAll(n => n.Equals(p.FileName, StringComparison.OrdinalIgnoreCase));
                    Log.Info("Turned on plugin " + p.Name + ".");
                }
                C.Settings.Save();
                MarkRestart();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't change " + p.Name + ":\n" + Errors.Friendly(ex) +
                                      (C.Server.IsAlive ? "\n\nStop the server first, then try again." : ""), "CardboardBox");
            }
            RefreshView();
        }

        private void Remove()
        {
            var p = Selected;
            if (p == null || !Confirm("Remove the plugin " + p.Name + " from this PC?\n\n(Its settings folder is kept, in case you add it back.)")) return;
            try
            {
                File.Delete(p.FullPath);
                C.Settings.DisabledPlugins.RemoveAll(n => n.Equals(p.FileName, StringComparison.OrdinalIgnoreCase));
                C.Settings.Save();
                Log.Info("Removed plugin " + p.Name + ".");
                MarkRestart();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't remove " + p.Name + ":\n" + Errors.Friendly(ex) +
                                      (C.Server.IsAlive ? "\n\nStop the server first, then try again." : ""), "CardboardBox");
            }
            RefreshView();
        }

        private void Upload()
        {
            var p = Selected;
            if (p == null) return;
            if (!Confirm("Upload " + p.FileName + " to the public GitHub repo " + C.Settings.RepoSlug + "?\n\n" +
                         "Every copy of CardboardBox using that repo will install it. Only upload plugins you're allowed to share.")) return;
            Run(() => C.UploadPluginToGitHubAsync(p.FullPath), _upload);
        }

        private void MarkRestart()
        {
            if (C.Server.IsAlive) C.RestartNeeded = true;
            C.RaiseChanged();
        }
    }
}
