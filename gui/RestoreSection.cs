using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // Restoring from a backup set: the list of backups for the identified device, the restore points of the chosen one, the
    // choice of files and the exact-restore option. The window shows its controls when Restore is selected and decides from
    // Ready whether Start is available.
    internal sealed class RestoreSection
    {
        readonly IRestoreSectionHost host;
        readonly ComboBox backups = new ComboBox { Name = "RestoreBackup", AccessibleName = "Available backups", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly ComboBox points = new ComboBox { Name = "RestorePoint", AccessibleName = "Full or incremental restore point", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly Button browse = GuiTheme.Button("Choose files…", false);
        readonly CheckBox exact = new CheckBox { Name = "ExactRestorePoint", Text = "Remove extra target files", AutoSize = true, ForeColor = GuiTheme.Ink, Anchor = AnchorStyles.Left };
        readonly Label notice = GuiTheme.Label("Choose a backup and a folder to restore your files.", 9, GuiTheme.Muted);
        Control backupField, details;
        Label backupCaption;
        RestoreSet set;
        string[] paths;
        string contextKey;
        int scan, pointScan;
        bool filling, loading, closed;
        M8SampleMatch pendingSample;

        internal RestoreSection(IRestoreSectionHost host) { this.host = host; }

        sealed class BackupItem
        {
            internal string Path, Label;
            public override string ToString() { return Label; }
        }

        // Whether a restore point (and, for "choose files", at least one file) is chosen, so Start can run.
        internal bool Ready
        {
            get
            {
                return !host.Busy && !loading && set != null && points.SelectedItem is RestorePoint &&
                    (host.RestoreScope != 1 || (paths != null && paths.Length != 0));
            }
        }
        internal bool ExactEnabled { get { return exact.Enabled; } }
        internal bool ExactChecked { get { return exact.Checked; } set { exact.Checked = value; } }

        internal Control BuildBackupField()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0) };
            backupCaption = GuiTheme.Caption("Available backups · all devices");
            panel.Controls.Add(backupCaption); panel.Controls.Add(backups);
            GuiTheme.StyleComboBox(backups); GuiTheme.StyleComboBox(points);
            backups.SelectedIndexChanged += delegate { if (!filling) LoadPoints(); };
            host.Hints.SetToolTip(backups, "All backups when no device is detected; matching backups when a device is detected. Detect again to refresh the list.");
            points.SelectedIndexChanged += delegate { paths = null; UpdateNotice(); };
            backupField = panel;
            return panel;
        }

        internal Control BuildDetails()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.Controls.Add(GuiTheme.Caption("Restore point · latest selected by default"), 0, 0);
            panel.Controls.Add(points, 0, 1);
            browse.Name = "BrowseRestoreFiles"; browse.Margin = new Padding(12, 0, 0, 0);
            panel.Controls.Add(browse, 1, 1);
            exact.Margin = new Padding(12, 0, 0, 0); panel.Controls.Add(exact, 1, 1);
            host.Hints.SetToolTip(exact, "Exact restore removes files and empty folders that are not in the saved point. You will confirm removals in the restore preview.");
            notice.Name = "RestoreNotice"; notice.MaximumSize = new System.Drawing.Size(750, 0); notice.Margin = new Padding(0, 8, 0, 10);
            panel.Controls.Add(notice, 0, 2); panel.SetColumnSpan(notice, 2);
            browse.Click += delegate {
                var point = points.SelectedItem as RestorePoint;
                if (point == null || loading) return;
                using (var dialog = new RestoreBrowserDialog(host.Window, point.Snapshot, paths))
                    if (dialog.ShowDialog(host.Window) == DialogResult.OK) { paths = dialog.SelectedPaths; UpdateNotice(); }
            };
            details = panel;
            return panel;
        }

        internal void SetEnabled(bool enabled) { backupField.Enabled = details.Enabled = enabled; }
        internal void Close() { closed = true; }
        // The next refresh reads the backups again even if nothing about the device or location changed.
        internal void ForgetContext() { contextKey = null; }

        // Shows the controls only while Restore is the selected operation.
        internal void RefreshControls()
        {
            bool restoring = host.SelectedOperation == GuiOperation.Restore;
            backupField.Visible = details.Visible = restoring;
            browse.Visible = restoring && host.RestoreScope == 1;
            exact.Visible = restoring && host.RestoreScope == 0;
            SetEnabled(!host.Busy);
            if (restoring) RefreshBackups();
        }

        // A sample found by the Sample check: select its backup and point, and its file when they are listed.
        internal void SelectSample(M8SampleMatch match)
        {
            pendingSample = match;
            exact.Checked = false;
            RefreshBackups(true);
        }

        internal void UpdateNotice()
        {
            int scope = host.RestoreScope;
            notice.Text = set == null ? "Choose a backup and a folder to restore your files." : set.Device.Name + " · " + set.Notice +
                (scope == 1 ? "  " + (paths == null ? "Click Choose files to select folders or files." : paths.Length + " file(s) or folder(s) selected.") : "  All files and folders in the selected point will be restored.");
            var selectedPoint = points.SelectedItem as RestorePoint;
            if (selectedPoint != null && selectedPoint.Id != "current")
                notice.Text += "  Taken " + selectedPoint.Snapshot.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + ".";
            browse.Enabled = !loading && !host.Busy && points.SelectedItem is RestorePoint;
            exact.Enabled = !loading && !host.Busy && set != null && selectedPoint != null;
            if (!exact.Enabled) exact.Checked = false;
            host.RefreshActionState();
        }

        // Lists the backups for the identified device (or all devices), unless nothing relevant changed since the last read.
        internal async void RefreshBackups(bool force = false)
        {
            if (!host.HasOptions || host.Busy || closed || !host.WindowReady) return;
            DeviceProfile device = host.DisplayedDevice;
            backupCaption.Text = device == null ? "Available backups · all devices" : "Backups for " + device.Name;
            backups.AccessibleName = backupCaption.Text;
            Config options;
            try { options = host.CaptureOptions(); } catch (Exception ex) when (ExpectedFailure.Is(ex)) { return; }
            string key = (device == null ? "" : device.Id) + "|" + options.BackupFolder;
            if (!force && key == contextKey) { if (set != null) UpdateNotice(); return; }
            bool sameContext = key == contextKey;
            contextKey = key;
            int current = ++scan;
            ++pointScan;
            var previous = sameContext ? backups.SelectedItem as BackupItem : null;
            string oldPath = previous == null ? null : previous.Path;
            loading = true; backups.Enabled = points.Enabled = browse.Enabled = false;
            host.RefreshActionState();
            set = null; paths = null;
            notice.Text = device == null ? "Loading backups for all devices…" : "Loading " + device.Name + " backups…";
            var found = new List<string>(); string error = null;
            try
            {
                found = await Task.Run(delegate {
                    using (new OperationContext(host.QuietInteraction(), CancellationToken.None) { PreviewOnly = true, Device = device }.Activate())
                        return RestoreCatalog.BackupsForDevice(options, device);
                });
            }
            catch (Exception ex) { error = ex.Message; } // shown in the notice
            host.RunOnUi(delegate { FinishBackups(current, found, oldPath, device, error); });
        }

        void FinishBackups(int current, List<string> found, string oldPath, DeviceProfile device, string error)
        {
            if (current != scan) return;
            filling = true;
            try
            {
                backups.Items.Clear(); points.Items.Clear();
                foreach (string path in found)
                {
                    string label = BackupSetNames.Label(path) + " · " + Path.GetFileName(Path.GetDirectoryName(path));
                    if (device == null)
                    {
                        try { var recorded = DeviceSelection.BackupDevice(path); if (recorded != null) label = recorded.Name + " · " + label; }
                        catch (IOException) { } // Loading the selected set reports a changed/unreadable record.
                        catch (UnauthorizedAccessException) { } // same: the selected set reports it
                    }
                    backups.Items.Add(new BackupItem { Path = path, Label = label });
                }
                int index = found.FindIndex(path => string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase));
                if (found.Count > 0) backups.SelectedIndex = index >= 0 ? index : 0;
            }
            finally { filling = false; }
            loading = false; backups.Enabled = found.Count > 0 && !host.Busy;
            if (found.Count == 0) notice.Text = error != null ? "Cannot list backups: " + error : device == null ? "No backups found at the configured backup locations." : "No backups found for " + device.Name + ".";
            else LoadPoints();
            host.RefreshActionState();
        }

        async void LoadPoints()
        {
            int current = ++pointScan;
            var backup = backups.SelectedItem as BackupItem;
            DeviceProfile device = host.DisplayedDevice;
            set = null; paths = null;
            points.Items.Clear();
            if (backup == null) { loading = false; UpdateNotice(); return; }
            loading = true; points.Enabled = browse.Enabled = false;
            host.RefreshActionState();
            notice.Text = "Loading restore points from SQLite…";
            RestoreSet loaded = null; string error = null;
            try { loaded = await Task.Run(delegate {
                using (new OperationContext(host.QuietInteraction(), CancellationToken.None) { PreviewOnly = true, Device = device }.Activate())
                    return RestoreCatalog.Load(backup.Path, device);
            }); }
            catch (Exception ex) { error = ex.Message; } // shown in the notice
            host.RunOnUi(delegate { FinishPoints(current, loaded, error); });
        }

        void FinishPoints(int current, RestoreSet loaded, string error)
        {
            if (current != pointScan) return;
            loading = false; set = loaded;
            if (loaded == null) { notice.Text = "Cannot read backup: " + error; return; }
            foreach (var point in loaded.Points) points.Items.Add(point);
            if (points.Items.Count > 0) points.SelectedIndex = 0;
            points.Enabled = !host.Busy && loaded.Points.Count > 0;
            if (pendingSample != null && string.Equals(loaded.Backup, pendingSample.Backup, StringComparison.OrdinalIgnoreCase))
            {
                var selected = pendingSample;
                pendingSample = null;
                var point = loaded.Points.Find(p => p.Id == selected.PointId);
                if (point == null || !point.Snapshot.Files.Exists(f => f.Relative == selected.Relative && f.Hash == selected.Hash))
                {
                    paths = null;
                    UpdateNotice();
                    notice.Text = "The selected sample or restore point changed. Run Sample check again.";
                    return;
                }
                points.SelectedItem = point;
                paths = new[] { selected.Relative };
                host.SetDetail("Sample selected. Review the restore destination, then click Restore.");
            }
            UpdateNotice();
        }

        // The restore the window should run: the chosen backup and point, and the files or exact removal asked for.
        internal RestoreRequest CaptureSelection()
        {
            var backup = backups.SelectedItem as BackupItem;
            var point = points.SelectedItem as RestorePoint;
            int scope = host.RestoreScope;
            if (loading || set == null || backup == null || point == null) throw new IOException("Choose a backup and restore point first.");
            if (scope == 1 && (paths == null || paths.Length == 0)) throw new IOException("Click Choose files and check the folders or files to restore.");
            return new RestoreRequest { Backup = backup.Path, Point = point.Id, Stamp = RestoreCatalog.Stamp(point.Snapshot), DeviceId = set.Device.Id,
                Paths = scope == 1 ? (string[])paths.Clone() : null, Exact = scope == 0 && exact.Enabled && exact.Checked };
        }
    }
}
