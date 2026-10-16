using System;
using System.Drawing;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // The Device tab: the detected device's picture, name and connection, what the app learned about its storage, and
    // the shortcuts to detect it and to go to Backup or Restore.
    internal sealed class DeviceCard
    {
        readonly IWorkflowTabHost host;
        readonly PictureBox image = new PictureBox { Name = "DeviceImage", Size = new Size(88, 88), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 4, 16, 0) };
        readonly Label name = GuiTheme.Label("No device detected", 10, GuiTheme.Muted);
        readonly Label connection = GuiTheme.Label("", 10, GuiTheme.Muted);
        readonly Label details = GuiTheme.Label("", 10, GuiTheme.Ink);
        Button goToBackup, goToRestore;
        TableLayoutPanel card;

        internal DeviceCard(IWorkflowTabHost host)
        {
            this.host = host;
            name.Name = "DetectedDevice"; connection.Name = "DeviceConnectionMode";
            name.MaximumSize = new Size(520, 0); connection.MaximumSize = new Size(520, 0);
            name.Margin = new Padding(0, 0, 0, 0);
            details.Name = "DeviceStorageDetails";
            details.MaximumSize = new Size(650, 0);
            details.Margin = new Padding(0, 12, 0, 10);
        }

        // The height the card needs, so the window can size the tab area.
        public int ContentHeight { get { return card == null ? 0 : card.Height; } }

        // The card, with the window's Detect button among its shortcuts. The two Go buttons call the actions given.
        internal Control Build(Button detect, Action showBackup, Action showRestore)
        {
            card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            card.SizeChanged += delegate { host.RefreshLayout(); };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.Controls.Add(image, 0, 0);
            var information = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            information.Controls.Add(name); information.Controls.Add(connection); information.Controls.Add(details);
            var shortcuts = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            goToBackup = GuiTheme.Button("Go to Backup", false); goToBackup.Name = "GoToBackup";
            goToRestore = GuiTheme.Button("Go to Restore", false); goToRestore.Name = "GoToRestore";
            goToBackup.Click += delegate { showBackup(); };
            goToRestore.Click += delegate { showRestore(); };
            detect.Name = "DetectDevice";
            shortcuts.Controls.Add(detect); shortcuts.Controls.Add(goToBackup); shortcuts.Controls.Add(goToRestore);
            information.Controls.Add(shortcuts);
            card.Controls.Add(information, 1, 0);
            Show(null, null);
            return card;
        }

        internal bool Built { get { return goToBackup != null; } }

        // In a device's own mode the Go buttons are not needed: that mode has the device's tabs only.
        internal void SetShortcutsVisible(bool visible) { goToBackup.Visible = goToRestore.Visible = visible; }

        // Shows a detected device's picture and name; null shows the general backup picture (at startup, and when
        // detection finds nothing). A device without a picture, or a build without the embedded pictures, keeps what's shown.
        internal void Show(DeviceProfile device, string mode)
        {
            details.Text = device == null ? "Connect your device and click Detect device. You can also back up a local folder or restore saved files without connecting a device." :
                "Click Detect device to refresh storage and connection details.";
            name.Text = device == null ? "No device detected" : device.Name + " · detected";
            name.Font = device == null ? GuiTheme.Font(10) : GuiTheme.Heading(17);
            name.ForeColor = device == null ? GuiTheme.Muted : GuiTheme.AccentDark;
            name.BackColor = device == null ? Color.Transparent : GuiTheme.AccentSoft;
            name.Padding = device == null ? Padding.Empty : new Padding(10, 4, 10, 5);
            connection.Text = mode ?? "";
            connection.Visible = device != null && !string.IsNullOrEmpty(mode);
            string picture = device == null ? "Backup" : device.IconName;
            if (picture == null) return;
            Image loaded = MainForm.LoadImage(picture + "-256.png");
            if (loaded == null) return;
            Image old = image.Image;
            image.Image = loaded;
            image.AccessibleName = device == null ? "No device detected" : device.Name;
            if (old != null) old.Dispose();
        }

        // Storage and connection details read from the device after detection.
        internal void ShowInformation(string text) { details.Text = text; host.RefreshLayout(); }

        internal void Close() { if (image.Image != null) image.Image.Dispose(); }
    }
}
