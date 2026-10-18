using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace CardboardBox.UI
{
    /// <summary>Colors, fonts and small factory helpers so every page looks the same.</summary>
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0xF4, 0xEF, 0xE6);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(0xE2, 0xD7, 0xC5);
        public static readonly Color Text = Color.FromArgb(0x2D, 0x2A, 0x26);
        public static readonly Color Muted = Color.FromArgb(0x7A, 0x70, 0x65);
        public static readonly Color Accent = Color.FromArgb(0xB0, 0x74, 0x36);
        public static readonly Color AccentDark = Color.FromArgb(0x8A, 0x57, 0x24);
        public static readonly Color AccentSoft = Color.FromArgb(0xF6, 0xE7, 0xD2);
        public static readonly Color Sidebar = Color.FromArgb(0x38, 0x2C, 0x23);
        public static readonly Color SidebarHover = Color.FromArgb(0x4A, 0x3B, 0x2F);
        public static readonly Color SidebarText = Color.FromArgb(0xEA, 0xDF, 0xCF);
        public static readonly Color Green = Color.FromArgb(0x2E, 0x9E, 0x5B);
        public static readonly Color Red = Color.FromArgb(0xC9, 0x4A, 0x3D);
        public static readonly Color Amber = Color.FromArgb(0xD3, 0x8F, 0x14);
        public static readonly Color Blue = Color.FromArgb(0x2F, 0x6F, 0xB0);
        public static readonly Color Gray = Color.FromArgb(0x9A, 0x92, 0x88);
        public static readonly Color ConsoleBg = Color.FromArgb(0x1D, 0x1B, 0x19);
        public static readonly Color ConsoleText = Color.FromArgb(0xE6, 0xE1, 0xD8);

        public static readonly Font Base = new Font("Segoe UI", 9.75f);
        public static readonly Font Small = new Font("Segoe UI", 8.75f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.75f);
        public static readonly Font H1 = new Font("Segoe UI Semibold", 17f);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 11.5f);
        public static readonly Font Big = new Font("Segoe UI Semibold", 18f);
        public static readonly Font Mono = new Font("Consolas", 9.75f);

        public static Icon AppIcon
        {
            get
            {
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CardboardBox.ico"))
                        return s != null ? new Icon(s) : SystemIcons.Application;
                }
                catch { return SystemIcons.Application; }
            }
        }

        public static Button Primary(string text) => MakeButton(text, Accent, Color.White, AccentDark);
        public static Button Secondary(string text) => MakeButton(text, Color.White, Text, Border);
        public static Button Danger(string text) => MakeButton(text, Red, Color.White, Color.FromArgb(0xA8, 0x36, 0x2B));
        public static Button Good(string text) => MakeButton(text, Green, Color.White, Color.FromArgb(0x23, 0x80, 0x48));

        private static Button MakeButton(string text, Color back, Color fore, Color border)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = fore,
                Font = Bold,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 4, 10, 4),
                Margin = new Padding(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.15f);
            b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.05f);
            // Flat buttons don't look disabled on their own - grey them out explicitly.
            b.EnabledChanged += (s, e) =>
            {
                b.BackColor = b.Enabled ? back : Color.FromArgb(0xEC, 0xE6, 0xDC);
                b.ForeColor = b.Enabled ? fore : Gray;
                b.FlatAppearance.BorderColor = b.Enabled ? border : Border;
                b.Cursor = b.Enabled ? Cursors.Hand : Cursors.Default;
            };
            return b;
        }

        public static Label Label(string text, Font font = null, Color? color = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = font ?? Base,
                ForeColor = color ?? Text,
                Margin = new Padding(0, 0, 0, 4),
                BackColor = Color.Transparent,
                UseMnemonic = false,
            };
        }

        /// <summary>A label that wraps inside its parent's width.</summary>
        public static Label Wrap(string text, Font font = null, Color? color = null)
        {
            var l = Label(text, font, color);
            l.AutoSize = true;
            l.MaximumSize = new Size(560, 0);
            return l;
        }

        public static LinkLabel Link(string text, Action onClick)
        {
            var l = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                Font = Base,
                LinkColor = AccentDark,
                ActiveLinkColor = Accent,
                VisitedLinkColor = AccentDark,
                Margin = new Padding(0, 4, 12, 4),
                BackColor = Color.Transparent,
            };
            l.LinkClicked += (s, e) => onClick();
            return l;
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            var f = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 0, 0, 4),
                BackColor = Color.Transparent,
            };
            f.Controls.AddRange(controls);
            return f;
        }

        /// <summary>A vertical stack that sizes itself to its contents.</summary>
        public static FlowLayoutPanel Stack(params Control[] controls)
        {
            var f = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };
            f.Controls.AddRange(controls);
            return f;
        }

        public static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("Couldn't open " + url + "\n\n" + ex.Message, "CardboardBox"); }
        }

        public static void OpenFolder(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { MessageBox.Show("Couldn't open the folder:\n" + ex.Message, "CardboardBox"); }
        }

        public static string Ago(DateTime utc)
        {
            if (utc == default(DateTime)) return "never";
            var span = DateTime.UtcNow - utc;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + " h ago";
            return utc.ToLocalTime().ToString("MMM d, HH:mm");
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + "s";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m " + t.Seconds + "s";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }
    }
}
