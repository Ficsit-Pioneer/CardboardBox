using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>Live server console + CardboardBox's own messages, with a box to type commands.</summary>
    public class ConsolePage : Page
    {
        private const int MaxLines = 4000;

        private readonly ConcurrentQueue<Tuple<string, Color>> _pending = new ConcurrentQueue<Tuple<string, Color>>();
        private readonly RichTextBox _box;
        private readonly TextBox _input;
        private readonly CheckBox _autoScroll;
        private readonly List<string> _history = new List<string>();
        private int _historyIndex;
        private readonly Timer _flush;

        public ConsolePage(MainForm main, AppController c)
            : base(main, c, "Console", "What the server is saying. Type commands at the bottom (no / needed).", 1)
        {
            Grid.Dock = DockStyle.Fill;
            Grid.AutoSize = false;
            AutoScroll = false;
            Grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _box = new RichTextBox
            {
                ReadOnly = true,
                BackColor = Theme.ConsoleBg,
                ForeColor = Theme.ConsoleText,
                Font = Theme.Mono,
                BorderStyle = BorderStyle.None,
                WordWrap = true,
                DetectUrls = false,
                HideSelection = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Margin = new Padding(0, 0, 12, 8),
            };
            var boxHost = new Panel { BackColor = Theme.ConsoleBg, Padding = new Padding(10, 8, 4, 8), Margin = new Padding(0, 0, 12, 8) };
            _box.Dock = DockStyle.Fill;
            boxHost.Controls.Add(_box);
            Place(boxHost);

            _input = new TextBox { Font = Theme.Mono, Width = 520, Margin = new Padding(0, 3, 8, 8) };
            _input.KeyDown += OnInputKey;
            var send = Theme.Primary("Send");
            send.Click += (s, e) => SendInput();
            var prompt = new Label { Text = ">", Font = Theme.Mono, AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            Place(Theme.Row(prompt, _input, send));

            _autoScroll = new CheckBox { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
            var quick = Theme.Row(
                Quick("Who's online", "list"),
                Quick("Save world", "save-all"),
                Quick("Server speed (TPS)", "tps"),
                Quick("Plugins", "plugins"),
                Theme.Secondary("Say something..."),
                Theme.Secondary("Clear"),
                Theme.Secondary("Open log file"),
                _autoScroll);
            var sayBtn = (Button)quick.Controls[4];
            sayBtn.Click += (s, e) => { _input.Text = "say "; _input.Focus(); _input.SelectionStart = _input.Text.Length; };
            ((Button)quick.Controls[5]).Click += (s, e) => _box.Clear();
            ((Button)quick.Controls[6]).Click += (s, e) => Theme.OpenFolder(AppPaths.Logs);
            Place(quick);

            _flush = new Timer { Interval = 150 };
            _flush.Tick += (s, e) => Flush();
            _flush.Start();
        }

        private Button Quick(string text, string command)
        {
            var b = Theme.Secondary(text);
            b.Click += (s, e) => Send(command);
            return b;
        }

        // ---------- incoming lines (any thread)

        public void AddServerLine(string line)
        {
            Color color = Theme.ConsoleText;
            if (line.Contains(" WARN]") || line.Contains("WARN]:")) color = Color.FromArgb(0xE8, 0xC0, 0x6A);
            else if (line.Contains(" ERROR]") || line.Contains("ERROR]:") || line.Contains("Exception")) color = Color.FromArgb(0xF0, 0x7A, 0x6E);
            else if (line.EndsWith("joined the game") || line.EndsWith("left the game")) color = Color.FromArgb(0x8F, 0xD8, 0x9A);
            else if (line.Contains("]: Done (")) color = Color.FromArgb(0x7E, 0xE0, 0x8C);
            Enqueue(line, color);
        }

        public void AddAppLine(LogLevel level, string msg)
        {
            Color color;
            switch (level)
            {
                case LogLevel.Success: color = Color.FromArgb(0x7E, 0xE0, 0x8C); break;
                case LogLevel.Warn: color = Color.FromArgb(0xE8, 0xC0, 0x6A); break;
                case LogLevel.Error: color = Color.FromArgb(0xF0, 0x7A, 0x6E); break;
                default: color = Color.FromArgb(0x8C, 0xC4, 0xF0); break;
            }
            Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + " CardboardBox] " + msg, color);
        }

        private void Enqueue(string text, Color color)
        {
            _pending.Enqueue(Tuple.Create(text, color));
            // Protect memory if the UI can't keep up (e.g. while minimized during a log storm).
            while (_pending.Count > MaxLines && _pending.TryDequeue(out _)) { }
        }

        // ---------- UI thread

        private void Flush()
        {
            if (_pending.IsEmpty || _box.IsDisposed) return;
            _box.SuspendLayout();
            int count = 0;
            while (count < 500 && _pending.TryDequeue(out var item))
            {
                _box.SelectionStart = _box.TextLength;
                _box.SelectionLength = 0;
                _box.SelectionColor = item.Item2;
                _box.AppendText(item.Item1 + "\n");
                count++;
            }

            if (_box.Lines.Length > MaxLines)
            {
                int removeUpTo = _box.GetFirstCharIndexFromLine(_box.Lines.Length - MaxLines + 500);
                if (removeUpTo > 0)
                {
                    _box.ReadOnly = false;
                    _box.Select(0, removeUpTo);
                    _box.SelectedText = "";
                    _box.ReadOnly = true;
                }
            }

            if (_autoScroll.Checked)
            {
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
            _box.ResumeLayout();
        }

        private void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { SendInput(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up && _history.Count > 0)
            {
                _historyIndex = Math.Max(0, _historyIndex - 1);
                _input.Text = _history[_historyIndex];
                _input.SelectionStart = _input.Text.Length;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && _history.Count > 0)
            {
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                _input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
                _input.SelectionStart = _input.Text.Length;
                e.SuppressKeyPress = true;
            }
        }

        private void SendInput()
        {
            var cmd = _input.Text.Trim();
            if (cmd.Length == 0) return;
            if (Send(cmd))
            {
                _history.Add(cmd);
                if (_history.Count > 100) _history.RemoveAt(0);
                _historyIndex = _history.Count;
                _input.Clear();
            }
        }

        private bool Send(string cmd)
        {
            if (!C.SendCommand(cmd)) return false;
            Enqueue("> " + cmd, Color.FromArgb(0xC9, 0xB3, 0x94));
            return true;
        }
    }
}
