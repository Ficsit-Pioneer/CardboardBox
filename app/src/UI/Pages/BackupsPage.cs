using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>Back up the world, restore it, and manage old backups.</summary>
    public class BackupsPage : Page
    {
        private readonly ListView _list;
        private readonly Button _backup, _restore, _delete;
        private readonly NumericUpDown _keep;

        public BackupsPage(MainForm main, AppController c)
            : base(main, c, "Backups", "Copies of your world, so nothing is ever lost. You can back up while the server is running.", 1)
        {
            var card = new Card(null);
            _list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                Width = 760, Height = 320, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Base,
            };
            _list.Columns.Add("Backup", 360);
            _list.Columns.Add("Made", 180);
            _list.Columns.Add("Size", 100);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();

            _backup = Theme.Primary("Back up now");
            _backup.Click += (s, e) => Run(async () => { await C.BackupAsync("manual"); RefreshView(); }, _backup);
            _restore = Theme.Secondary("Restore selected...");
            _restore.Click += (s, e) => Restore();
            _delete = Theme.Secondary("Delete selected");
            _delete.Click += (s, e) => Delete();
            var folder = Theme.Secondary("Open backups folder");
            folder.Click += (s, e) => Theme.OpenFolder(AppPaths.Backups);

            _keep = new NumericUpDown { Minimum = 1, Maximum = 200, Width = 70, Margin = new Padding(0, 4, 8, 0) };
            _keep.ValueChanged += (s, e) => { C.Settings.KeepBackups = (int)_keep.Value; C.Settings.Save(); };

            card.Add(_list,
                Theme.Row(_backup, _restore, _delete, folder),
                Theme.Row(new Label { Text = "Keep the newest", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, _keep,
                          new Label { Text = "backups (older ones are removed automatically)", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }),
                Hint("A backup is also made automatically before GitHub updates are applied (you can turn that off in Server Settings)."));
            Place(card);

            VisibleChanged += (s, e) => { if (Visible) RefreshView(); };
            Resize += (s, e) => _list.Width = Math.Max(500, ClientSize.Width - Padding.Horizontal - 60);
        }

        private BackupInfo Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as BackupInfo : null;

        public override void RefreshView()
        {
            if (!Visible) return;
            _keep.Value = Math.Max(_keep.Minimum, Math.Min(_keep.Maximum, C.Settings.KeepBackups));
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var b in Backups.List())
                _list.Items.Add(new ListViewItem(new[] { b.Name, b.Created.ToString("ddd d MMM yyyy, HH:mm"), FileUtil.FormatBytes(b.Size) }) { Tag = b });
            if (_list.Items.Count == 0)
                _list.Items.Add(new ListViewItem(new[] { "No backups yet", "", "" }) { ForeColor = Theme.Gray });
            _list.EndUpdate();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            _restore.Enabled = Selected != null;
            _delete.Enabled = Selected != null;
        }

        private void Restore()
        {
            var b = Selected;
            if (b == null) return;
            if (!Confirm("Put the world back to how it was at " + b.Created.ToString("HH:mm on ddd d MMM") + "?\n\n" +
                         "The server will be stopped first. The current world is NOT deleted - it's moved into the backups folder.")) return;
            Run(async () =>
            {
                if (C.Server.IsAlive) await C.StopAsync();
                await System.Threading.Tasks.Task.Run(() => Backups.Restore(b));
                MessageBox.Show(this, "Restored! Press Start on the Dashboard to play.", "CardboardBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }, _restore);
        }

        private void Delete()
        {
            var b = Selected;
            if (b == null || !Confirm("Delete the backup " + b.Name + "? This can't be undone.")) return;
            try { File.Delete(b.Path); Log.Info("Deleted backup " + b.Name + "."); }
            catch (Exception ex) { MessageBox.Show(this, Errors.Friendly(ex), "CardboardBox"); }
            RefreshView();
        }
    }
}
