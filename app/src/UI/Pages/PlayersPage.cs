using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI.Pages
{
    /// <summary>Who's online, plus one-click op / kick / ban / whitelist.</summary>
    public class PlayersPage : Page
    {
        private static readonly Regex ValidName = new Regex("^[A-Za-z0-9_]{2,16}$");

        private readonly ListBox _online, _ops, _whitelist, _banned;
        private readonly TextBox _name;
        private readonly Label _note;

        public PlayersPage(MainForm main, AppController c)
            : base(main, c, "Players", "Manage the people on your server. These buttons only work while the server is running.", 2)
        {
            var onlineCard = new Card("Online now");
            _online = MakeList(260);
            _online.SelectedIndexChanged += (s, e) => { if (_online.SelectedItem is string n) _name.Text = n; };
            var refresh = Theme.Secondary("Refresh");
            refresh.Click += (s, e) => { C.SendCommand("list"); };
            onlineCard.Add(_online, refresh);
            Place(onlineCard);

            var actions = new Card("Do something to a player");
            _name = new TextBox { Width = 240, Font = Theme.Base };
            _note = Hint("");
            actions.Add(
                FieldLabel("Player name"), _name,
                Hint("Pick someone from a list, or type a name."),
                FieldLabel("Permissions"),
                Theme.Row(Act("Make operator (admin)", "op {0}"), Act("Remove operator", "deop {0}")),
                FieldLabel("Whitelist"),
                Theme.Row(Act("Add to whitelist", "whitelist add {0}"), Act("Remove from whitelist", "whitelist remove {0}")),
                FieldLabel("Moderation"),
                Theme.Row(Act("Kick", "kick {0} Kicked by the server admin"), Act("Ban", "ban {0} Banned by the server admin", true), Act("Unban", "pardon {0}")),
                FieldLabel("Other"),
                Theme.Row(Act("Heal", "effect give {0} minecraft:instant_health 1 10"),
                          Act("Survival mode", "gamemode survival {0}"), Act("Creative mode", "gamemode creative {0}")),
                _note);
            Place(actions);

            var lists = new Card("Lists saved on the server");
            _ops = MakeList(130);
            _whitelist = MakeList(130);
            _banned = MakeList(130);
            foreach (var lb in new[] { _ops, _whitelist, _banned })
                lb.SelectedIndexChanged += (s, e) => { if (((ListBox)s).SelectedItem is string n) _name.Text = n; };
            lists.Add(Theme.Row(
                Theme.Stack(FieldLabel("Operators"), _ops),
                Theme.Stack(FieldLabel("Whitelist"), _whitelist),
                Theme.Stack(FieldLabel("Banned"), _banned)));
            Place(lists, 2);

            VisibleChanged += (s, e) => { if (Visible) RefreshView(); };
        }

        private static ListBox MakeList(int height) =>
            new ListBox { Width = 260, Height = height, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Base, IntegralHeight = false, Margin = new Padding(0, 0, 12, 8) };

        private Button Act(string text, string command, bool danger = false)
        {
            var b = danger ? Theme.Danger(text) : Theme.Secondary(text);
            b.Click += (s, e) =>
            {
                var name = _name.Text.Trim();
                if (!ValidName.IsMatch(name))
                {
                    MessageBox.Show(this, "Type a valid Minecraft name first (2-16 letters, numbers or _).", "CardboardBox");
                    return;
                }
                if (danger && !Confirm("Really ban " + name + "?")) return;
                if (C.SendCommand(string.Format(command, name)))
                {
                    Log.Info("Sent: " + string.Format(command, name));
                    var t = new Timer { Interval = 1500 };
                    t.Tick += (_, __) => { t.Dispose(); RefreshView(); };
                    t.Start();
                }
                else MessageBox.Show(this, "Start the server first - these actions are carried out by the running server.", "CardboardBox");
            };
            return b;
        }

        public override void RefreshView()
        {
            if (!Visible) return;
            Fill(_online, C.Server.IsAlive ? C.Server.Players : new List<string>(), C.Server.IsAlive ? "Nobody online" : "Server is stopped");
            Fill(_ops, ReadNames("ops.json"), "None");
            Fill(_whitelist, ReadNames("whitelist.json"), "Empty");
            Fill(_banned, ReadNames("banned-players.json"), "Nobody");
            _note.Text = C.Server.IsAlive ? "" : "Start the server to use these buttons.";
        }

        private static void Fill(ListBox box, List<string> names, string empty)
        {
            var selected = box.SelectedItem as string;
            box.BeginUpdate();
            box.Items.Clear();
            if (names.Count == 0) { box.Items.Add("(" + empty + ")"); box.Enabled = false; }
            else
            {
                box.Enabled = true;
                foreach (var n in names) box.Items.Add(n);
                if (selected != null && names.Contains(selected)) box.SelectedItem = selected;
            }
            box.EndUpdate();
        }

        private static List<string> ReadNames(string file)
        {
            var path = Path.Combine(AppPaths.Server, file);
            var result = new List<string>();
            try
            {
                if (!File.Exists(path)) return result;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
                foreach (var item in Json.ParseArray(text).OfType<Dictionary<string, object>>())
                {
                    var n = item.Str("name");
                    if (!string.IsNullOrEmpty(n)) result.Add(n);
                }
            }
            catch { }
            return result.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
