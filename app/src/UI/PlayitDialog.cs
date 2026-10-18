using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using CardboardBox.Core;

namespace CardboardBox.UI
{
    /// <summary>Walks the person through linking CardboardBox to playit.gg (one approval in the browser).</summary>
    public class PlayitDialog : Form
    {
        private readonly AppController _c;
        private readonly Label _status, _address, _hint;
        private readonly ProgressBar _spinner;
        private readonly LinkLabel _openAgain;
        private readonly Button _copy, _retry, _close;
        private CancellationTokenSource _cts;
        private string _code;

        public PlayitDialog(AppController controller)
        {
            _c = controller;
            Text = "Let friends join from anywhere";
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            BackColor = Theme.Card;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 330);

            var title = new Label { Text = "Share your server with playit.gg", Font = Theme.H2, AutoSize = true, Location = new Point(20, 16) };
            var body = new Label
            {
                Text = "playit.gg is a free service that gives your server an address friends can use from anywhere, " +
                       "with no router setup.\n\nYour browser will open. Log in or make a free playit.gg account, " +
                       "then approve CardboardBox. Come back here when you're done.",
                AutoSize = false, Size = new Size(520, 92), Location = new Point(20, 48), ForeColor = Theme.Text, UseMnemonic = false,
            };
            _spinner = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Location = new Point(20, 150), Size = new Size(520, 10) };
            _status = new Label { Text = "Starting...", Font = Theme.Bold, AutoSize = false, Size = new Size(520, 40), Location = new Point(20, 168), UseMnemonic = false };
            _openAgain = Theme.Link("Browser didn't open? Click here to open the playit.gg page", () => { if (_code != null) Theme.OpenUrl(Playit.ClaimUrl(_code)); });
            _openAgain.Location = new Point(20, 208);

            _address = new Label { Font = Theme.Big, ForeColor = Theme.Green, AutoSize = true, Location = new Point(18, 200), Visible = false, UseMnemonic = false };
            _copy = Theme.Secondary("Copy address");
            _copy.Location = new Point(20, 244);
            _copy.Visible = false;
            _copy.Click += (s, e) => { try { Clipboard.SetText(_address.Text); _copy.Text = "Copied!"; } catch { } };
            _hint = new Label { AutoSize = false, Size = new Size(520, 36), Location = new Point(20, 244), ForeColor = Theme.Muted, Font = Theme.Small, Visible = false, UseMnemonic = false };

            _retry = Theme.Primary("Try again");
            _retry.Location = new Point(330, 284);
            _retry.Visible = false;
            _retry.Click += (s, e) => Begin();
            _close = Theme.Secondary("Cancel");
            _close.Location = new Point(450, 284);
            _close.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { title, body, _spinner, _status, _openAgain, _address, _copy, _hint, _retry, _close });
            CancelButton = _close;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Begin();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            base.OnFormClosing(e);
        }

        private async void Begin()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _code = Playit.NewClaimCode();
            _retry.Visible = false;
            _address.Visible = false;
            _copy.Visible = false;
            _hint.Visible = false;
            _openAgain.Visible = true;
            _spinner.Visible = true;
            _status.ForeColor = Theme.Text;
            _status.Text = "Opening playit.gg in your browser...";
            _close.Text = "Cancel";
            Theme.OpenUrl(Playit.ClaimUrl(_code));

            try
            {
                var tunnel = await _c.SetupPlayitAsync(_code, s => { try { BeginInvoke((Action)(() => _status.Text = s)); } catch { } }, token);
                _spinner.Visible = false;
                _openAgain.Visible = false;
                _close.Text = "Done";
                if (tunnel != null)
                {
                    _status.Text = "All set! Friends anywhere can join your server at:";
                    _address.Text = tunnel.Address;
                    _address.Visible = true;
                    _copy.Visible = true;
                    _copy.Text = "Copy address";
                    if (!_c.Server.IsAlive)
                    {
                        _hint.Text = "The address works while the server is running. Press Start on the Dashboard.";
                        _hint.Location = new Point(150, 250);
                        _hint.Size = new Size(390, 36);
                        _hint.Visible = true;
                    }
                }
                else
                {
                    _status.Text = "Linked! playit.gg is still preparing your address. It will show on the Dashboard once the server is running.";
                }
            }
            catch (OperationCanceledException) { /* closed */ }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _spinner.Visible = false;
                _status.ForeColor = Theme.Red;
                _status.Text = "That didn't work: " + Errors.Friendly(ex);
                _status.Size = new Size(520, 70);
                _openAgain.Visible = false;
                _retry.Visible = true;
                _close.Text = "Close";
            }
        }
    }
}
