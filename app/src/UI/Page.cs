using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI
{
    /// <summary>Base class for the pages on the left menu.</summary>
    public class Page : UserControl
    {
        protected readonly AppController C;
        protected readonly MainForm Main;
        protected readonly TableLayoutPanel Grid;

        public string Title { get; }

        public Page(MainForm main, AppController controller, string title, string subtitle, int columns = 2)
        {
            Main = main;
            C = controller;
            Title = title;
            BackColor = Theme.Bg;
            Dock = DockStyle.Fill;
            AutoScroll = true;
            Padding = new Padding(24, 18, 12, 12);
            Font = Theme.Base;

            Grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = columns,
                BackColor = Color.Transparent,
            };
            for (int i = 0; i < columns; i++) Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));

            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.Transparent };
            header.Controls.Add(new Label { Text = subtitle, Font = Theme.Base, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(0, 38) });
            header.Controls.Add(new Label { Text = title, Font = Theme.H1, ForeColor = Theme.Text, AutoSize = true, Location = new Point(-2, 0) });

            Controls.Add(Grid);
            Controls.Add(header);
        }

        /// <summary>Adds a card to the grid. span = how many columns it covers.</summary>
        protected T Place<T>(T control, int span = 1) where T : Control
        {
            control.Dock = DockStyle.Fill;
            Grid.Controls.Add(control);
            if (span > 1) Grid.SetColumnSpan(control, span);
            return control;
        }

        /// <summary>Called whenever something changes (on the UI thread).</summary>
        public virtual void RefreshView() { }

        /// <summary>Called once a second while this page is visible.</summary>
        public virtual void Tick() { }

        /// <summary>Runs an async action from a button, showing any error in a friendly box.</summary>
        protected async void Run(Func<Task> action, Control busyControl = null)
        {
            if (busyControl != null) busyControl.Enabled = false;
            try { await action(); }
            catch (Exception ex)
            {
                Log.Error("Action failed", ex);
                MessageBox.Show(this, Errors.Friendly(ex), "CardboardBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (busyControl != null && !busyControl.IsDisposed) busyControl.Enabled = true;
                RefreshView();
            }
        }

        protected bool Confirm(string text, string title = "CardboardBox") =>
            MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        protected static Label FieldLabel(string text) =>
            new Label { Text = text, AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Text, Margin = new Padding(0, 8, 0, 2), UseMnemonic = false };

        protected static Label Hint(string text) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Font = Theme.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 4), UseMnemonic = false };
    }
}
