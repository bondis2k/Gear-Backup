using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    readonly TextBox restoreArchive = new TextBox { Name = "RestoreArchivePath", ReadOnly = true, Dock = DockStyle.Fill };
    readonly Button browseArchiveRestore = GuiTheme.Button("Browse…", false);
    readonly Label archiveRestoreNotice = GuiTheme.Label("Choose a ZIP archive and detect the destination device.", 9, GuiTheme.Muted);
    Control archiveRestoreField, archiveRestoreDetails;
    ZipRestoreInfo archiveRestoreInfo;
    bool loadingArchive;
    int archiveRead;
    internal Func<string> ArchiveSourcePicker { get; set; }
    internal Action<SourceChoice> ArchiveRestoreDeviceValidator { get; set; }

    Control BuildArchiveRestoreField()
    {
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 16, 0) };
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        field.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.Controls.Add(FieldLabel("ZIP archive"), 0, 0); field.SetColumnSpan(field.Controls[0], 2);
        field.Controls.Add(new FieldPanel(restoreArchive) { Dock = DockStyle.Fill }, 0, 1);
        browseArchiveRestore.Name = "BrowseRestoreArchive"; browseArchiveRestore.Margin = new Padding(8, 0, 0, 0);
        field.Controls.Add(browseArchiveRestore, 1, 1);
        browseArchiveRestore.Click += delegate { ChooseRestoreArchive(); };
        hints.SetToolTip(restoreArchive, "Standalone Gear Backup ZIP with gearbackup-info.txt and checksums.sha256.");
        archiveRestoreField = field;
        return field;
    }

    Control BuildArchiveRestoreDetails()
    {
        var panel = new Panel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
        archiveRestoreNotice.Name = "ZipRestoreNotice";
        archiveRestoreNotice.Dock = DockStyle.Top; archiveRestoreNotice.MaximumSize = new System.Drawing.Size(750, 0);
        archiveRestoreNotice.Margin = new Padding(0, 8, 0, 10); panel.Controls.Add(archiveRestoreNotice);
        archiveRestoreDetails = panel;
        return panel;
    }

    async void ChooseRestoreArchive()
    {
        if (runner.IsBusy) return;
        string path;
        if (ArchiveSourcePicker != null) path = ArchiveSourcePicker();
        else using (var picker = new OpenFileDialog { Title = "Choose ZIP archive to restore", Filter = "ZIP archive (*.zip)|*.zip",
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true })
            path = picker.ShowDialog(this) == DialogResult.OK ? picker.FileName : null;
        if (string.IsNullOrEmpty(path)) return;
        restoreArchive.Text = path; archiveRestoreInfo = null; loadingArchive = true;
        hints.SetToolTip(restoreArchive, path);
        restoreArchive.Select(path.Length, 0); restoreArchive.ScrollToCaret();
        int read = ++archiveRead;
        archiveRestoreNotice.Text = "Reading ZIP device information…"; RefreshRestoreActionState();
        ZipRestoreInfo info = null; string error = null;
        try
        {
            info = await Task.Run(delegate {
                using (new OperationContext(new GuiInteraction(this, p => { }), CancellationToken.None) { PreviewOnly = true }.Activate())
                    return ZipRestore.Inspect(path);
            });
        }
        catch (Exception ex) { error = ex.Message; }
        RestoreUi(delegate {
            if (read != archiveRead) return;
            loadingArchive = false; archiveRestoreInfo = info;
            if (info == null) archiveRestoreNotice.Text = "Cannot restore this ZIP: " + error;
            else UpdateArchiveRestoreNotice();
            RefreshRestoreActionState();
        });
    }

    void UpdateArchiveRestoreNotice()
    {
        if (loadingArchive || archiveRestoreInfo == null) return;
        string summary = archiveRestoreInfo.Device.Name + " · " + archiveRestoreInfo.Files + " files · " +
            Formatting.FormatSize(archiveRestoreInfo.ContentBytes) + " content · " + Formatting.FormatSize(archiveRestoreInfo.ExtractedBytes) + " extracted · taken " +
            archiveRestoreInfo.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + ". ";
        archiveRestoreNotice.Text = summary + (displayedDevice == null ? "Detect the destination device to enable restore." :
            displayedDevice.Id != archiveRestoreInfo.Device.Id ? "Device mismatch: detected " + displayedDevice.Name + ". Restore is blocked." :
            displayedDevice.Mtp ? "Matching device. Choose a PC folder, then transfer restored files manually; direct MTP restore is unavailable." :
            "Matching device. Files are merged into the destination after checksum and free-space checks.");
    }

    bool ArchiveRestoreReady { get { return !runner.IsBusy && !loadingArchive && archiveRestoreInfo != null &&
        displayedDevice != null && displayedDevice.Id == archiveRestoreInfo.Device.Id &&
        (!displayedDevice.Mtp || !string.IsNullOrWhiteSpace(restoreDestination.Text)); } }

    internal void ValidateArchiveRestoreDevice(SourceChoice known)
    {
        if (ArchiveRestoreDeviceValidator != null) { ArchiveRestoreDeviceValidator(known); return; }
        SourceChoice current = Session.DetectSource();
        if (current == null || current.Detected == null || current.Detected.Id != known.Detected.Id ||
            (known.Mtp == null && (current.Mtp != null || PathSafety.Rooted(current.Path) != PathSafety.Rooted(known.Path) || current.PnpId != known.PnpId)) ||
            (known.Mtp != null && (current.Mtp == null || current.Mtp.Info.InstanceId != known.Mtp.Info.InstanceId)))
            throw new IOException("The detected device changed. Detect the destination device again before restoring the ZIP.");
    }
}
