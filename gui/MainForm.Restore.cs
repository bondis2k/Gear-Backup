using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    readonly ComboBox restoreBackup = new ComboBox { Name = "RestoreBackup", AccessibleName = "Available backups", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox restorePoint = new ComboBox { Name = "RestorePoint", AccessibleName = "Full or incremental restore point", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button browseRestore = GuiTheme.Button("Choose files…", false);
    readonly CheckBox restoreExact = new CheckBox { Name = "ExactRestorePoint", Text = "Remove extra target files", AutoSize = true, ForeColor = GuiTheme.Ink, Anchor = AnchorStyles.Left };
    readonly Label restoreNotice = GuiTheme.Label("Choose a backup and a folder to restore your files.", 9, GuiTheme.Muted);
    Control restoreBackupField, restoreDetails;
    Label restoreBackupCaption;
    RestoreSet restoreSet;
    string[] restorePaths;
    string restoreContextKey;
    int restoreScan, restorePointScan, rememberedRestoreScope;
    bool fillingRestore, loadingRestore;

    sealed class RestoreBackupItem
    {
        internal string Path, Label;
        public override string ToString() { return Label; }
    }

    Control BuildRestoreBackupField()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0) };
        restoreBackupCaption = FieldLabel("Available backups · all devices");
        panel.Controls.Add(restoreBackupCaption); panel.Controls.Add(restoreBackup);
        GuiTheme.StyleComboBox(restoreBackup); GuiTheme.StyleComboBox(restorePoint);
        restoreBackup.SelectedIndexChanged += delegate { if (!fillingRestore) LoadRestorePoints(); };
        hints.SetToolTip(restoreBackup, "All backups when no device is detected; matching backups when a device is detected. Detect again to refresh the list.");
        restorePoint.SelectedIndexChanged += delegate { restorePaths = null; UpdateRestoreSelectionNotice(); };
        restoreBackupField = panel;
        return panel;
    }
    Control BuildRestoreDetails()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(FieldLabel("Restore point · latest selected by default"), 0, 0);
        panel.Controls.Add(restorePoint, 0, 1);
        browseRestore.Name = "BrowseRestoreFiles"; browseRestore.Margin = new Padding(12, 0, 0, 0);
        panel.Controls.Add(browseRestore, 1, 1);
        restoreExact.Margin = new Padding(12, 0, 0, 0); panel.Controls.Add(restoreExact, 1, 1);
        hints.SetToolTip(restoreExact, "Exact restore removes files and empty folders that are not in the saved point. You will confirm removals in the restore preview.");
        restoreNotice.MaximumSize = new System.Drawing.Size(750, 0); restoreNotice.Margin = new Padding(0, 8, 0, 10);
        panel.Controls.Add(restoreNotice, 0, 2); panel.SetColumnSpan(restoreNotice, 2);
        browseRestore.Click += delegate {
            var point = restorePoint.SelectedItem as RestorePoint;
            if (point == null || loadingRestore) return;
            using (var dialog = new RestoreBrowserDialog(this, point.Snapshot, restorePaths))
                if (dialog.ShowDialog(this) == DialogResult.OK) { restorePaths = dialog.SelectedPaths; UpdateRestoreSelectionNotice(); }
        };
        restoreDetails = panel;
        return panel;
    }
    void RefreshRestoreControls()
    {
        bool restoring = Selected == GuiOperation.Restore;
        archiveRestoreField.Visible = archiveRestoreDetails.Visible = Selected == GuiOperation.RestoreZip;
        archiveRestoreField.Enabled = !runner.IsBusy; UpdateArchiveRestoreNotice();
        restoreBackupField.Visible = restoreDetails.Visible = restoring;
        browseRestore.Visible = restoring && rememberedRestoreScope == 1;
        restoreExact.Visible = restoring && rememberedRestoreScope == 0;
        restoreBackupField.Enabled = restoreDetails.Enabled = !runner.IsBusy;
        if (restoring) RefreshRestoreBackups();
    }
    void UpdateRestoreSelectionNotice()
    {
        restoreNotice.Text = restoreSet == null ? "Choose a backup and a folder to restore your files." : restoreSet.Device.Name + " · " + restoreSet.Notice +
            (rememberedRestoreScope == 1 ? "  " + (restorePaths == null ? "Click Choose files to select folders or files." : restorePaths.Length + " file(s) or folder(s) selected.") : "  All files and folders in the selected point will be restored.");
        var selectedPoint = restorePoint.SelectedItem as RestorePoint;
        if (selectedPoint != null && selectedPoint.Id != "current")
            restoreNotice.Text += "  Taken " + selectedPoint.Snapshot.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + ".";
        browseRestore.Enabled = !loadingRestore && !runner.IsBusy && restorePoint.SelectedItem is RestorePoint;
        var point = restorePoint.SelectedItem as RestorePoint;
        restoreExact.Enabled = !loadingRestore && !runner.IsBusy && restoreSet != null && point != null && (point.Id != "current" || restoreSet.Single);
        if (!restoreExact.Enabled) restoreExact.Checked = false;
        RefreshRestoreActionState();
    }
    void RefreshRestoreActionState()
    {
        if (Selected == GuiOperation.RestoreZip) { start.Enabled = preview.Enabled = ArchiveRestoreReady; return; }
        // Restore holds its action buttons back until a point is chosen; the other operations take them back.
        if (Selected != GuiOperation.Restore)
        {
            start.Enabled = !runner.IsBusy;
            preview.Enabled = !runner.IsBusy && Selected != GuiOperation.Check;
            return;
        }
        bool ready = !runner.IsBusy && !loadingRestore && restoreSet != null && restorePoint.SelectedItem is RestorePoint &&
            (rememberedRestoreScope != 1 || (restorePaths != null && restorePaths.Length != 0));
        start.Enabled = preview.Enabled = ready;
    }
    async void RefreshRestoreBackups(bool force = false)
    {
        if (config == null || runner.IsBusy || IsDisposed || !IsHandleCreated) return;
        DeviceProfile device = displayedDevice;
        restoreBackupCaption.Text = device == null ? "Available backups · all devices" : "Backups for " + device.Name;
        restoreBackup.AccessibleName = restoreBackupCaption.Text;
        Config options;
        try { options = CaptureConfig(); } catch (Exception) { return; }
        string key = (device == null ? "" : device.Id) + "|" + options.BackupFolder;
        if (!force && key == restoreContextKey) { if (restoreSet != null) UpdateRestoreSelectionNotice(); return; }
        bool sameContext = key == restoreContextKey;
        restoreContextKey = key;
        int scan = ++restoreScan;
        ++restorePointScan;
        var previous = sameContext ? restoreBackup.SelectedItem as RestoreBackupItem : null;
        string oldPath = previous == null ? null : previous.Path;
        loadingRestore = true; restoreBackup.Enabled = restorePoint.Enabled = browseRestore.Enabled = false;
        RefreshRestoreActionState();
        restoreSet = null; restorePaths = null;
        restoreNotice.Text = device == null ? "Loading backups for all devices…" : "Loading " + device.Name + " backups…";
        var paths = new List<string>(); string error = null;
        try
        {
            paths = await Task.Run(delegate {
                using (new OperationContext(new GuiInteraction(this, value => { }), CancellationToken.None) { PreviewOnly = true, Device = device }.Activate())
                    return RestoreCatalog.BackupsForDevice(options, device);
            });
        }
        catch (Exception ex) { error = ex.Message; }
        RestoreUi(delegate { FinishRestoreBackups(scan, paths, oldPath, device, error); });
    }
    void FinishRestoreBackups(int scan, List<string> paths, string oldPath, DeviceProfile device, string error)
    {
        if (scan != restoreScan) return;
        fillingRestore = true;
        try
        {
            restoreBackup.Items.Clear(); restorePoint.Items.Clear();
            foreach (string path in paths)
            {
                string label = BackupSetNames.Label(path) + " · " + Path.GetFileName(Path.GetDirectoryName(path));
                if (device == null)
                {
                    try { var recorded = DeviceSelection.BackupDevice(path); if (recorded != null) label = recorded.Name + " · " + label; }
                    catch (IOException) { } // Loading the selected set reports a changed/unreadable record.
                    catch (UnauthorizedAccessException) { }
                }
                restoreBackup.Items.Add(new RestoreBackupItem { Path = path, Label = label });
            }
            int index = paths.FindIndex(path => string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase));
            if (paths.Count > 0) restoreBackup.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { fillingRestore = false; }
        loadingRestore = false; restoreBackup.Enabled = paths.Count > 0 && !runner.IsBusy;
        if (paths.Count == 0) restoreNotice.Text = error != null ? "Cannot list backups: " + error : device == null ? "No backups found at the configured backup locations." : "No backups found for " + device.Name + " at the configured backup locations.";
        else LoadRestorePoints();
        RefreshRestoreActionState();
    }
    async void LoadRestorePoints()
    {
        int scan = ++restorePointScan;
        var backup = restoreBackup.SelectedItem as RestoreBackupItem;
        DeviceProfile device = displayedDevice;
        restoreSet = null; restorePaths = null;
        restorePoint.Items.Clear();
        if (backup == null) { loadingRestore = false; UpdateRestoreSelectionNotice(); return; }
        loadingRestore = true; restorePoint.Enabled = browseRestore.Enabled = false;
        RefreshRestoreActionState();
        restoreNotice.Text = "Loading restore points from SQLite…";
        RestoreSet set = null; string error = null;
        try { set = await Task.Run(delegate {
            using (new OperationContext(new GuiInteraction(this, value => { }), CancellationToken.None) { PreviewOnly = true, Device = device }.Activate())
                return RestoreCatalog.Load(backup.Path, device);
        }); }
        catch (Exception ex) { error = ex.Message; }
        RestoreUi(delegate { FinishRestorePoints(scan, set, error); });
    }
    void FinishRestorePoints(int scan, RestoreSet set, string error)
    {
        if (scan != restorePointScan) return;
        loadingRestore = false; restoreSet = set;
        if (set == null) { restoreNotice.Text = "Cannot read backup: " + error; return; }
        foreach (var point in set.Points) restorePoint.Items.Add(point);
        if (restorePoint.Items.Count > 0) restorePoint.SelectedIndex = 0;
        restorePoint.Enabled = !runner.IsBusy && set.Points.Count > 0;
        UpdateRestoreSelectionNotice();
    }
    void RestoreUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (!InvokeRequired) { action(); return; }
        try { BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing) action(); }); }
        catch (InvalidOperationException) { } // The window closed while the read-only scan finished.
    }
    RestoreRequest CaptureRestoreSelection()
    {
        var backup = restoreBackup.SelectedItem as RestoreBackupItem;
        var point = restorePoint.SelectedItem as RestorePoint;
        if (loadingRestore || restoreSet == null || backup == null || point == null) throw new IOException("Choose a backup and restore point first.");
        if (rememberedRestoreScope == 1 && (restorePaths == null || restorePaths.Length == 0)) throw new IOException("Click Choose files and check the folders or files to restore.");
        return new RestoreRequest { Backup = backup.Path, Point = point.Id, Stamp = RestoreCatalog.Stamp(point.Snapshot), DeviceId = restoreSet.Device.Id,
            Paths = rememberedRestoreScope == 1 ? (string[])restorePaths.Clone() : null, Exact = rememberedRestoreScope == 0 && restoreExact.Enabled && restoreExact.Checked };
    }
}
