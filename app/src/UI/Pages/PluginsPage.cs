using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>Plugins (Paper/Purpur) or mods (Fabric/NeoForge/Forge): list, add, turn off, remove, find on Modrinth.</summary>
    public class PluginsPage : Page
    {
        private readonly ListView _list, _results;
        private readonly Button _add, _toggle, _remove, _upload, _search, _install;
        private readonly TextBox _query;
        private readonly CheckBox _forEveryone;
        private readonly Label _kind, _searchStatus;

        private class AddonRow
        {
            public string FileName;   // without .disabled
            public string FullPath;
            public bool Enabled;
            public string Name, Version, Source;
        }

        public PluginsPage(MainForm main, AppController c)
            : base(main, c, "Plugins & Mods", "Add features to the server. Changes take effect the next time the server starts.", 1)
        {
            // ---------- installed
            var card = new Card(null);
            _kind = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Font = Theme.Bold, UseMnemonic = false, Margin = new Padding(0, 0, 0, 8) };
            _list = MakeList(240);
            _list.Columns.Add("Name", 200);
            _list.Columns.Add("Version", 120);
            _list.Columns.Add("Status", 70);
            _list.Columns.Add("Comes from", 240);
            _list.Columns.Add("File", 240);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += (s, e) => Toggle();

            _add = Theme.Primary("Add file (.jar)...");
            _add.Click += (s, e) => AddFiles();
            _toggle = Theme.Secondary("Turn off");
            _toggle.Click += (s, e) => Toggle();
            _remove = Theme.Secondary("Remove");
            _remove.Click += (s, e) => Remove();
            _upload = Theme.Secondary("Upload to GitHub");
            _upload.Click += (s, e) => Upload();
            var folder = Theme.Secondary("Open folder");
            folder.Click += (s, e) => Theme.OpenFolder(Path.Combine(AppPaths.Server, C.Software.AddonFolder ?? "plugins"));
            card.Add(_kind, _list, Theme.Row(_add, _toggle, _remove, _upload, folder));
            Place(card);

            // ---------- find more on Modrinth
            var find = new Card("Find and install (from Modrinth)");
            _query = new TextBox { Width = 360 };
            _query.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            _search = Theme.Secondary("Search");
            _search.Click += (s, e) => Search();
            _results = MakeList(220);
            _results.Columns.Add("Name", 200);
            _results.Columns.Add("Downloads", 90);
            _results.Columns.Add("What it does", 560);
            _results.SelectedIndexChanged += (s, e) => _install.Enabled = _results.SelectedItems.Count == 1 && _results.SelectedItems[0].Tag != null;
            _results.DoubleClick += (s, e) => Install();
            _install = Theme.Primary("Install selected");
            _install.Enabled = false;
            _install.Click += (s, e) => Install();
            _forEveryone = new CheckBox { Text = "Also add it to the GitHub list, so every CardboardBox server gets it", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            _searchStatus = Hint("Type what you're looking for (for example \"homes\", \"maps\" or \"performance\") or press Search to see the most popular.");
            find.Add(Theme.Row(_query, _search), _searchStatus, _results, Theme.Row(_install, _forEveryone));
            Place(find);

            VisibleChanged += (s, e) => { if (Visible) RefreshView(); };
            Resize += (s, e) =>
            {
                var w = Math.Max(500, ClientSize.Width - Padding.Horizontal - 60);
                _list.Width = w;
                _results.Width = w;
            };
        }

        private static ListView MakeList(int height) => new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            Width = 860, Height = height, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Base,
        };

        private string Folder => C.Software.AddonFolder;

        // ------------------------------------------------------------------ installed list

        private IEnumerable<AddonRow> Scan()
        {
            if (Folder == null) yield break;
            var dir = Path.Combine(AppPaths.Server, Folder);
            if (!Directory.Exists(dir)) yield break;
            var managed = new HashSet<string>(C.State.ManagedPlugins.Values.Select(v => Path.GetFileName(v)), StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(dir))
            {
                bool enabled = path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                bool disabled = path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
                if (!enabled && !disabled) continue;
                var fileName = Path.GetFileName(enabled ? path : path.Substring(0, path.Length - ".disabled".Length));
                var row = new AddonRow { FileName = fileName, FullPath = path, Enabled = enabled };
                ReadMetadata(path, row);
                if (C.State.SyncedFiles.ContainsKey(Folder + "\\" + fileName)) row.Source = "GitHub repo";
                else if (managed.Contains(fileName)) row.Source = "Installed automatically";
                else row.Source = "Added on this PC";
                yield return row;
            }
        }

        /// <summary>Reads name + version from plugin.yml, paper-plugin.yml, fabric.mod.json or (neo)forge mods.toml.</summary>
        private static void ReadMetadata(string jar, AddonRow row)
        {
            row.Name = Path.GetFileNameWithoutExtension(row.FileName);
            try
            {
                using (var fs = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    string Read(string entry)
                    {
                        var e = zip.GetEntry(entry);
                        if (e == null) return null;
                        using (var r = new StreamReader(e.Open())) return r.ReadToEnd();
                    }

                    var yml = Read("paper-plugin.yml") ?? Read("plugin.yml");
                    if (yml != null)
                    {
                        row.Name = Match(yml, @"^name:\s*['""]?([^'""\r\n]+)") ?? row.Name;
                        row.Version = Match(yml, @"^version:\s*['""]?([^'""\r\n]+)");
                        return;
                    }
                    var fabric = Read("fabric.mod.json");
                    if (fabric != null)
                    {
                        var d = Json.ParseObject(fabric);
                        row.Name = d.Str("name") ?? row.Name;
                        row.Version = d.Str("version");
                        return;
                    }
                    var toml = Read("META-INF/neoforge.mods.toml") ?? Read("META-INF/mods.toml");
                    if (toml != null)
                    {
                        row.Name = Match(toml, @"^\s*displayName\s*=\s*""([^""]+)""") ?? row.Name;
                        var v = Match(toml, @"^\s*version\s*=\s*""([^""]+)""");
                        row.Version = v != null && !v.Contains("${") ? v : null;
                    }
                }
            }
            catch { /* not a readable jar - still list it */ }
        }

        private static string Match(string text, string pattern)
        {
            var m = Regex.Match(text, pattern, RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public override void RefreshView()
        {
            if (!Visible) return;
            var sw = C.Software;
            _kind.Text = sw.AddonFolder == null
                ? "Your server runs " + sw.Name + ", which can't use plugins or mods. Change the server type in Server Settings to add some."
                : "Your server runs " + sw.Name + ", so it uses " + sw.AddonWord + "." +
                  (sw.AddonFolder == "mods" && sw.Id != "fabric" ? " Most mods also need to be installed in each player's game." : "");
            _add.Enabled = _search.Enabled = _query.Enabled = sw.AddonFolder != null;
            _add.Text = "Add " + (sw.AddonFolder == "mods" ? "mod" : "plugin") + " file (.jar)...";
            _forEveryone.Visible = C.Settings.HasToken;

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
                _list.Items.Add(new ListViewItem(new[] { "Nothing here yet", "", "", "Find some below", "" }) { ForeColor = Theme.Gray });
            _list.EndUpdate();
            UpdateButtons();
        }

        private AddonRow Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as AddonRow : null;

        private void UpdateButtons()
        {
            var p = Selected;
            _toggle.Enabled = p != null;
            _toggle.Text = p != null && !p.Enabled ? "Turn on" : "Turn off";
            _remove.Enabled = p != null && p.Source == "Added on this PC";
            _upload.Enabled = p != null && C.Settings.HasToken && p.Source == "Added on this PC";
        }

        private void AddFiles()
        {
            if (Folder == null) return;
            using (var dlg = new OpenFileDialog { Title = "Choose " + C.Software.AddonWord, Filter = "Minecraft " + C.Software.AddonWord + " (*.jar)|*.jar", Multiselect = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Directory.CreateDirectory(Path.Combine(AppPaths.Server, Folder));
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        var dest = Path.Combine(AppPaths.Server, Folder, Path.GetFileName(file));
                        if (File.Exists(dest) && !Confirm(Path.GetFileName(file) + " already exists. Replace it?")) continue;
                        File.Copy(file, dest, true);
                        C.Settings.DisabledPlugins.Remove(Path.GetFileName(file));
                        Log.Success("Added " + Path.GetFileName(file) + ".");
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
                var on = Path.Combine(Path.GetDirectoryName(p.FullPath), p.FileName);
                var off = on + ".disabled";
                if (p.Enabled)
                {
                    if (File.Exists(off)) File.Delete(off);
                    File.Move(on, off);
                    if (!C.Settings.DisabledPlugins.Contains(p.FileName)) C.Settings.DisabledPlugins.Add(p.FileName);
                    Log.Info("Turned off " + p.Name + ".");
                }
                else
                {
                    if (File.Exists(on)) File.Delete(on);
                    File.Move(off, on);
                    C.Settings.DisabledPlugins.RemoveAll(n => n.Equals(p.FileName, StringComparison.OrdinalIgnoreCase));
                    Log.Info("Turned on " + p.Name + ".");
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
            if (p == null || !Confirm("Remove " + p.Name + " from this PC?\n\n(Its settings folder is kept, in case you add it back.)")) return;
            try
            {
                File.Delete(p.FullPath);
                C.Settings.DisabledPlugins.RemoveAll(n => n.Equals(p.FileName, StringComparison.OrdinalIgnoreCase));
                C.Settings.Save();
                Log.Info("Removed " + p.Name + ".");
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
                         "Every copy of CardboardBox using that repo will install it. Only upload files you're allowed to share.")) return;
            Run(() => C.UploadPluginToGitHubAsync(p.FullPath, Folder), _upload);
        }

        private void MarkRestart()
        {
            if (C.Server.IsAlive) C.RestartNeeded = true;
            C.RaiseChanged();
        }

        // ------------------------------------------------------------------ Modrinth search

        private void Search()
        {
            if (Folder == null) return;
            var sw = C.Software;
            var mc = C.State.InstalledMcVersion;
            if (mc == null) { _searchStatus.Text = "Press Start once first, so CardboardBox knows the Minecraft version."; return; }
            Run(async () =>
            {
                _searchStatus.Text = "Searching...";
                var hits = await ModrinthSearch.SearchAsync(_query.Text.Trim(), sw, mc);
                _results.BeginUpdate();
                _results.Items.Clear();
                foreach (var h in hits)
                    _results.Items.Add(new ListViewItem(new[] { h.Title, h.DownloadsText, h.Description }) { Tag = h });
                if (hits.Count == 0) _results.Items.Add(new ListViewItem(new[] { "Nothing found", "", "Try other words." }) { ForeColor = Theme.Gray });
                _results.EndUpdate();
                _searchStatus.Text = hits.Count == 0 ? "No " + sw.AddonWord + " found for " + sw.Name + " " + mc + "."
                    : "Showing " + sw.AddonWord + " that work with " + sw.Name + " " + mc + ". Pick one and press Install.";
            }, _search);
        }

        private void Install()
        {
            if (_results.SelectedItems.Count != 1 || !(_results.SelectedItems[0].Tag is ModrinthHit hit)) return;
            bool everyone = _forEveryone.Visible && _forEveryone.Checked;
            Run(async () =>
            {
                var files = await C.InstallFromModrinthAsync(hit.Slug, everyone);
                MessageBox.Show(this, "Installed " + hit.Title + (files.Count > 1 ? " (plus " + (files.Count - 1) + " required add-on" + (files.Count > 2 ? "s" : "") + ")" : "") + "." +
                                      (C.Server.IsAlive ? "\n\nRestart the server to use it." : "") +
                                      (everyone ? "\n\nIt was also added to the GitHub list for every server." : ""),
                    "CardboardBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
                MarkRestart();
            }, _install);
        }
    }
}
