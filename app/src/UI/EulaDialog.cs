using System.Drawing;
using System.Windows.Forms;

namespace CardboardBox.UI
{
    /// <summary>Mojang requires whoever runs a server to agree to the Minecraft EULA. We ask once.</summary>
    public class EulaDialog : Form
    {
        public EulaDialog()
        {
            Text = "Minecraft EULA";
            Icon = Theme.AppIcon;
            Font = Theme.Base;
            BackColor = Theme.Card;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 250);

            var title = new Label { Text = "One quick thing before the first start", Font = Theme.H2, AutoSize = true, Location = new Point(20, 18) };
            var body = new Label
            {
                Text = "To run a Minecraft server you must agree to Mojang's Minecraft End User License Agreement (EULA). " +
                       "Please read it, then tick the box if you agree. CardboardBox only asks once.",
                AutoSize = false, Size = new Size(480, 60), Location = new Point(20, 52), ForeColor = Theme.Text,
            };
            var link = Theme.Link("Read the Minecraft EULA (opens your browser)", () => Theme.OpenUrl("https://aka.ms/MinecraftEULA"));
            link.Location = new Point(20, 112);
            var agree = new CheckBox { Text = "I have read and agree to the Minecraft EULA", AutoSize = true, Location = new Point(20, 148), Font = Theme.Bold };
            var ok = Theme.Primary("Start the server");
            ok.Enabled = false;
            ok.Location = new Point(250, 196);
            ok.DialogResult = DialogResult.OK;
            var cancel = Theme.Secondary("Cancel");
            cancel.Location = new Point(410, 196);
            cancel.DialogResult = DialogResult.Cancel;
            agree.CheckedChanged += (s, e) => ok.Enabled = agree.Checked;

            Controls.AddRange(new Control[] { title, body, link, agree, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
