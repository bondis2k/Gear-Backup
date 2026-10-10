using System.Drawing;
using System.Windows.Forms;

partial class GearBackup
{
    sealed class AboutDialog : Form
    {
        public AboutDialog()
        {
            Text = "About " + AppIdentity.Name;
            Font = new Font("Segoe UI", 10);
            BackColor = GuiTheme.Background;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 235);
            if (GuiHeadless) Opacity = 0;
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24),
                FlowDirection = FlowDirection.TopDown, WrapContents = false };
            layout.Controls.Add(GuiTheme.Label(AppIdentity.Name, 23, GuiTheme.Ink));
            var version = GuiTheme.Label("Version " + AppIdentity.Version, 12, GuiTheme.Accent);
            version.Name = "ApplicationVersion";
            layout.Controls.Add(version);
            layout.Controls.Add(GuiTheme.Label("Backup and restore for your music gear and camera.", 10, GuiTheme.Muted));
            var close = GuiTheme.Button("Close", true);
            close.DialogResult = DialogResult.OK;
            layout.Controls.Add(close);
            AcceptButton = close; CancelButton = close;
            Controls.Add(layout);
        }
    }
}
