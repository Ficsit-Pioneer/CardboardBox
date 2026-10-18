using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CardboardBox.UI
{
    /// <summary>White rounded box with a title - the building block of every page.</summary>
    public class Card : Panel
    {
        public readonly FlowLayoutPanel Body;
        private readonly Label _title;

        public Card(string title)
        {
            BackColor = Theme.Card;
            Padding = new Padding(16, 12, 16, 12);
            Margin = new Padding(0, 0, 12, 12);
            DoubleBuffered = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            Body = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = Color.Transparent,
            };
            _title = new Label { Text = title, Font = Theme.H2, ForeColor = Theme.Text, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
            Controls.Add(Body);
            if (!string.IsNullOrEmpty(title)) Controls.Add(_title);
        }

        public void Add(params Control[] controls) => Body.Controls.AddRange(controls);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Little line chart for CPU / RAM history.</summary>
    public class Sparkline : Control
    {
        public IList<double> Values = new List<double>();
        public double Max = 100;
        public Color LineColor = Theme.Accent;
        public IList<double> SecondValues;
        public Color SecondColor = Theme.Gray;

        public Sparkline()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 36;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var grid = new Pen(Color.FromArgb(0xEE, 0xE7, 0xDC)))
            {
                g.DrawLine(grid, 0, Height - 1, Width, Height - 1);
                g.DrawLine(grid, 0, Height / 2, Width, Height / 2);
            }
            if (SecondValues != null) Draw(g, SecondValues, SecondColor, false);
            Draw(g, Values, LineColor, true);
        }

        private void Draw(Graphics g, IList<double> values, Color color, bool fill)
        {
            if (values == null || values.Count < 2 || Max <= 0) return;
            int n = Core.StatsSampler.HistoryLength;
            float dx = (Width - 2) / (float)(n - 1);
            var pts = new List<PointF>();
            int start = n - values.Count;
            for (int i = 0; i < values.Count; i++)
            {
                float x = 1 + (start + i) * dx;
                float y = (float)(Height - 2 - Math.Min(1, values[i] / Max) * (Height - 4));
                pts.Add(new PointF(x, y));
            }
            if (fill)
            {
                var poly = new List<PointF>(pts) { new PointF(pts.Last().X, Height - 1), new PointF(pts[0].X, Height - 1) };
                using (var b = new SolidBrush(Color.FromArgb(40, color))) g.FillPolygon(b, poly.ToArray());
            }
            using (var pen = new Pen(color, fill ? 1.8f : 1.2f)) g.DrawLines(pen, pts.ToArray());
        }
    }

    /// <summary>A dashboard tile: small title, big value, optional detail line and chart.</summary>
    public class StatTile : Panel
    {
        public readonly Label Title, Value, Detail;
        public readonly Sparkline Chart;

        public StatTile(string title, bool withChart = false)
        {
            BackColor = Theme.Card;
            Padding = new Padding(14, 10, 14, 10);
            Margin = new Padding(0, 0, 12, 12);
            Dock = DockStyle.Fill;
            DoubleBuffered = true;

            Title = new Label { Text = title.ToUpperInvariant(), Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Dock = DockStyle.Top };
            Value = new Label { Text = "-", Font = Theme.Big, ForeColor = Theme.Text, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 2, 0, 0) };
            Detail = new Label { Text = "", Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, Dock = DockStyle.Top, Height = 34 };
            if (withChart)
            {
                Chart = new Sparkline { Dock = DockStyle.Bottom, Height = 34 };
                Controls.Add(Chart);
            }
            Controls.Add(Detail);
            Controls.Add(Value);
            Controls.Add(Title);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Shows the MOTD the way the Minecraft server list does (dark box, icon, colored text).</summary>
    public class MotdPreview : Control
    {
        private static readonly Dictionary<char, Color> Colors = new Dictionary<char, Color>
        {
            ['0'] = Color.FromArgb(0, 0, 0), ['1'] = Color.FromArgb(0, 0, 170), ['2'] = Color.FromArgb(0, 170, 0),
            ['3'] = Color.FromArgb(0, 170, 170), ['4'] = Color.FromArgb(170, 0, 0), ['5'] = Color.FromArgb(170, 0, 170),
            ['6'] = Color.FromArgb(255, 170, 0), ['7'] = Color.FromArgb(170, 170, 170), ['8'] = Color.FromArgb(85, 85, 85),
            ['9'] = Color.FromArgb(85, 85, 255), ['a'] = Color.FromArgb(85, 255, 85), ['b'] = Color.FromArgb(85, 255, 255),
            ['c'] = Color.FromArgb(255, 85, 85), ['d'] = Color.FromArgb(255, 85, 255), ['e'] = Color.FromArgb(255, 255, 85),
            ['f'] = Color.FromArgb(255, 255, 255),
        };

        public string ServerName = "Minecraft Server";
        public string Motd = "";
        public Image Icon;
        public string PlayersText = "0/20";

        public MotdPreview()
        {
            DoubleBuffered = true;
            Height = 84;
            Width = 460;
            Font = new Font("Consolas", 10f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(0x24, 0x22, 0x20));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var iconRect = new Rectangle(10, 10, 64, 64);
            if (Icon != null) g.DrawImage(Icon, iconRect);
            else using (var b = new SolidBrush(Color.FromArgb(0x55, 0x55, 0x55))) g.FillRectangle(b, iconRect);

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(g, ServerName, Font, new Point(84, 10), Color.White);
            var size = TextRenderer.MeasureText(PlayersText, Font);
            TextRenderer.DrawText(g, PlayersText, Font, new Point(Width - size.Width - 8, 10), Color.FromArgb(170, 170, 170));

            var lines = (Motd ?? "").Replace("\r", "").Split('\n');
            for (int i = 0; i < Math.Min(2, lines.Length); i++)
                DrawColored(g, lines[i], 84, 32 + i * 20);
        }

        private void DrawColored(Graphics g, string text, int x, int y)
        {
            var color = Color.FromArgb(170, 170, 170);
            bool bold = false;
            var sb = new System.Text.StringBuilder();
            Action flush = () =>
            {
                if (sb.Length == 0) return;
                using (var f = new Font(Font, bold ? FontStyle.Bold : FontStyle.Regular))
                {
                    var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                    TextRenderer.DrawText(g, sb.ToString(), f, new Point(x, y), color, flags);
                    x += TextRenderer.MeasureText(g, sb.ToString(), f, Size.Empty, flags).Width;
                }
                sb.Clear();
            };
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c == '§' || c == '&') && i + 1 < text.Length)
                {
                    char code = char.ToLowerInvariant(text[i + 1]);
                    if (Colors.ContainsKey(code)) { flush(); color = Colors[code]; bold = false; i++; continue; }
                    if (code == 'l') { flush(); bold = true; i++; continue; }
                    if (code == 'r') { flush(); color = Color.FromArgb(170, 170, 170); bold = false; i++; continue; }
                    if ("kmno".IndexOf(code) >= 0) { i++; continue; }
                }
                sb.Append(c);
            }
            flush();
        }
    }
}
