using System.Drawing;
using System.Windows.Forms;
using static GearBackup.Gui.GearGui;

namespace GearBackup.Gui
{
    internal sealed class AboutDialog : Form
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
            ClientSize = new Size(560, 300);
            if (GuiHeadless) Opacity = 0;
            // The app's icon (the one in the title bar) on the left, the name and details beside it.
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 28, 32, 24), ColumnCount = 2, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var picture = new PictureBox { Name = "AboutIcon", AccessibleName = AppIdentity.Name, Size = new Size(120, 120), SizeMode = PictureBoxSizeMode.Zoom,
                Image = MainForm.LoadImage("Backup-256.png"), Margin = new Padding(0, 4, 28, 0) };
            FormClosed += delegate { if (picture.Image != null) picture.Image.Dispose(); };
            layout.Controls.Add(picture, 0, 0);
            var details = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            details.Controls.Add(GuiTheme.Label(AppIdentity.Name, 25, GuiTheme.Ink));
            var version = GuiTheme.Label("Version " + AppIdentity.Version, 12, GuiTheme.Accent);
            version.Name = "ApplicationVersion";
            details.Controls.Add(version);
            var description = GuiTheme.Label("Backup and restore for your music gear and camera.", 10, GuiTheme.Muted);
            description.MaximumSize = new Size(340, 0);
            details.Controls.Add(description);
            var licence = GuiTheme.Label("Free software under the GNU General Public License, version 3.", 9, GuiTheme.Muted);
            licence.Name = "ApplicationLicense";
            licence.MaximumSize = new Size(340, 0);
            details.Controls.Add(licence);
            layout.Controls.Add(details, 1, 0);
            var close = GuiTheme.Button("Close", true);
            close.DialogResult = DialogResult.OK;
            close.Anchor = AnchorStyles.Right;
            close.Margin = new Padding(0, 12, 0, 0);
            layout.Controls.Add(close, 0, 1);
            layout.SetColumnSpan(close, 2);
            AcceptButton = close; CancelButton = close;
            Controls.Add(layout);
            GuiTheme.ApplyTo(this);
        }
    }

}
