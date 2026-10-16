using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // Restoring a standalone ZIP: choosing the archive, reading its device information in the background, and deciding
    // whether the detected device matches. The window shows its controls when that operation is selected.
    internal sealed class ZipRestoreSection
    {
        readonly IRestoreSectionHost host;
        readonly TextBox archivePath = new TextBox { Name = "RestoreArchivePath", ReadOnly = true, Dock = DockStyle.Fill };
        readonly Button browse = GuiTheme.Button("Browse…", false);
        readonly Label notice = GuiTheme.Label("Choose a ZIP archive and detect the destination device.", 9, GuiTheme.Muted);
        Control field, details;
        ZipRestoreInfo info;
        bool loading, closed;
        int reads;
        CancellationTokenSource inspection;
        // Tests replace the file dialog, the reader and the device check.
        internal Func<string> SourcePicker { get; set; }
        internal Func<string, CancellationToken, ZipRestoreInfo> Inspector { get; set; }
        internal Action<SourceChoice> DeviceValidator { get; set; }

        internal ZipRestoreSection(IRestoreSectionHost host) { this.host = host; }

        internal string ArchivePath { get { return archivePath.Text; } }
        internal ZipRestoreInfo Info { get { return info; } }
        internal bool Loading { get { return loading; } }

        // Whether Start can run: a readable archive for the identified device (and a folder, for an MTP device).
        internal bool Ready
        {
            get
            {
                DeviceProfile device = host.DisplayedDevice;
                return !host.Busy && !loading && info != null && device != null && device.Id == info.Device.Id &&
                    (!device.Mtp || !string.IsNullOrWhiteSpace(host.RestoreDestinationText));
            }
        }

        internal Control BuildField()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 16, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.Controls.Add(GuiTheme.Caption("ZIP archive"), 0, 0); panel.SetColumnSpan(panel.Controls[0], 2);
            panel.Controls.Add(new FieldPanel(archivePath) { Dock = DockStyle.Fill }, 0, 1);
            browse.Name = "BrowseRestoreArchive"; browse.Margin = new Padding(8, 0, 0, 0);
            panel.Controls.Add(browse, 1, 1);
            browse.Click += delegate { Choose(); };
            host.Hints.SetToolTip(archivePath, "Standalone Gear Backup ZIP with gearbackup-info.txt and checksums.sha256.");
            field = panel;
            return panel;
        }

        internal Control BuildDetails()
        {
            var panel = new Panel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
            notice.Name = "ZipRestoreNotice";
            notice.Dock = DockStyle.Top; notice.MaximumSize = new System.Drawing.Size(750, 0);
            notice.Margin = new Padding(0, 8, 0, 10); panel.Controls.Add(notice);
            details = panel;
            return panel;
        }

        // Shows the controls only while ZIP restore is the selected operation.
        internal void SetVisible(bool visible)
        {
            field.Visible = details.Visible = visible;
            field.Enabled = !host.Busy;
            UpdateNotice();
        }

        internal void Close()
        {
            closed = true;
            if (inspection != null) inspection.Cancel();
        }

        internal async void Choose()
        {
            if (host.Busy) return;
            string path;
            if (SourcePicker != null) path = SourcePicker();
            else using (var picker = new OpenFileDialog { Title = "Choose ZIP archive to restore", Filter = "ZIP archive (*.zip)|*.zip",
                CheckFileExists = true, Multiselect = false, RestoreDirectory = true })
                path = picker.ShowDialog(host.Owner) == DialogResult.OK ? picker.FileName : null;
            if (string.IsNullOrEmpty(path)) return;
            archivePath.Text = path; info = null; loading = true;
            if (inspection != null) inspection.Cancel();
            var current = new CancellationTokenSource(); inspection = current;
            host.Hints.SetToolTip(archivePath, path);
            archivePath.Select(path.Length, 0); archivePath.ScrollToCaret();
            int read = ++reads;
            notice.Text = "Reading ZIP device information…"; host.RefreshActionState();
            ZipRestoreInfo result = null; string error = null;
            try
            {
                result = await Task.Run(delegate {
                    using (new OperationContext(host.QuietInteraction(), current.Token) { PreviewOnly = true }.Activate())
                        return Inspector == null ? ZipRestore.Inspect(path) : Inspector(path, current.Token);
                });
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                if (ReferenceEquals(inspection, current)) inspection = null;
                current.Dispose();
            }
            host.RunOnUi(delegate {
                if (read != reads || closed) return;
                loading = false; info = result;
                if (result == null) notice.Text = "Cannot restore this ZIP: " + error;
                else UpdateNotice();
                host.RefreshActionState();
            });
        }

        // Before the restore writes anything: the device found now must still be the one the archive was checked against.
        internal void ValidateDevice(SourceChoice known)
        {
            if (DeviceValidator != null) { DeviceValidator(known); return; }
            SourceChoice current = Session.DetectSource();
            if (current == null || current.Detected == null || current.Detected.Id != known.Detected.Id ||
                (known.Mtp == null && (current.Mtp != null || PathSafety.Rooted(current.Path) != PathSafety.Rooted(known.Path) || current.PnpId != known.PnpId)) ||
                (known.Mtp != null && (current.Mtp == null || current.Mtp.Info.InstanceId != known.Mtp.Info.InstanceId)))
                throw new System.IO.IOException("The detected device changed. Detect the destination device again before restoring the ZIP.");
        }

        internal void UpdateNotice()
        {
            if (loading || info == null) return;
            DeviceProfile device = host.DisplayedDevice;
            string summary = info.Device.Name + " · " + info.Files + " files · " +
                Formatting.FormatSize(info.ContentBytes) + " content · " + Formatting.FormatSize(info.ExtractedBytes) + " extracted · taken " +
                info.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + ". ";
            notice.Text = summary + (device == null ? "Detect the destination device to enable restore." :
                device.Id != info.Device.Id ? "Device mismatch: detected " + device.Name + ". Restore is blocked." :
                device.Mtp ? "Matching device. Choose a PC folder, then transfer restored files manually; direct MTP restore is unavailable." :
                "Matching device. Files are merged into the destination after checksum and free-space checks.");
        }
    }
}
