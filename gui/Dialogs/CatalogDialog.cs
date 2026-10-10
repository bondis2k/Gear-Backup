using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

partial class GearBackup
{
    sealed class CatalogDialog : ToolsDialog
    {
        readonly Config config;
        readonly bool storage;
        readonly DataGridView grid;
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly DataGridView cleanup = ToolGrid("CleanupItems");
        BackupCatalog catalog;
        public CatalogDialog(MainForm owner, Config options, bool storageMode, BackupCatalog initial = null)
            : base(owner, storageMode ? "Storage" : "Dashboard", storageMode ? "See what uses space and review cleanup before recycling items." : "Backup health across your devices and profiles. Backups older than 7 days are marked overdue.")
        {
            config = options; storage = storageMode; grid = ToolGrid(storage ? "StorageTable" : "DashboardTable");
            if (storage)
            {
                foreach (string column in new[] { "Profile / backup", "Current files", "Old versions", "History objects", "Metadata", "Unique content", "Location" }) grid.Columns.Add(column, column);
                var usage = new TabPage("Space usage"); var clean = new TabPage("Cleanup");
                usage.Controls.Add(grid); clean.Controls.Add(cleanup); tabs.TabPages.Add(usage); tabs.TabPages.Add(clean); ToolLayout.Controls.Add(tabs, 0, 1);
                cleanup.ReadOnly = false;
                cleanup.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Select", FillWeight = 15 });
                foreach (string column in new[] { "Kind", "Size", "Location" }) { int i = cleanup.Columns.Add(column, column); cleanup.Columns[i].ReadOnly = true; }
                cleanup.Columns["Location"].FillWeight = 280;
                ToolButton("Preview cleanup", "PreviewCleanup", ReviewCleanup);
            }
            else
            {
                foreach (string column in new[] { "Profile", "Last successful backup", "Latest status", "Last check", "Check result", "Stored size", "Attention", "Location" }) grid.Columns.Add(column, column);
                ToolLayout.Controls.Add(grid, 0, 1);
                ToolButton("Check latest backup", "CheckCatalogBackup", delegate {
                    if (grid.CurrentRow == null) return;
                    var row = grid.CurrentRow.Tag as CatalogBackup; if (row == null) return;
                    Work(() => { using (LockHistory(row.History)) return new IntegrityEngine().Run(new List<string> { row.Path }, OperationContext.Current); },
                        result => { Notice.Text = result.Summary; RefreshCatalog(); });
                });
                ToolButton("View recipes", "ViewCatalogRecipes", delegate {
                    if (grid.CurrentRow == null) return;
                    var row = grid.CurrentRow.Tag as CatalogBackup; if (row == null) return;
                    using (var viewer = new RecipeViewerDialog(owner, row.Path)) viewer.ShowDialog(this);
                });
            }
            ToolButton("Refresh", "RefreshCatalog", RefreshCatalog);
            ToolButton("Open folder", "OpenCatalogFolder", delegate {
                if (grid.CurrentRow == null) return;
                var row = grid.CurrentRow.Tag as CatalogBackup;
                if (row != null && Directory.Exists(row.Path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Path) { UseShellExecute = true });
            });
            if (initial != null) Populate(initial); else Shown += delegate { RefreshCatalog(); };
        }
        void RefreshCatalog() { Work(() => ScanBackupCatalog(config), Populate); }
        internal void Populate(BackupCatalog value)
        {
            catalog = value; grid.Rows.Clear(); cleanup.Rows.Clear();
            if (storage)
            {
                foreach (CatalogBackup row in value.Backups)
                {
                    int i = grid.Rows.Add(row.Profile + " / " + Path.GetFileName(row.Path), FormatSize(row.FilesBytes), FormatSize(row.VersionsBytes),
                        FormatSize(row.ObjectsBytes), FormatSize(row.MetadataBytes), FormatSize(row.UniqueBytes), row.Path);
                    grid.Rows[i].Tag = row;
                }
                grid.Rows.Add("ZIP archives", FormatSize(value.ZipBytes)); grid.Rows.Add("MTP mirrors", FormatSize(value.MirrorBytes));
                foreach (StorageItem item in value.Cleanup) { int i = cleanup.Rows.Add(false, item.Kind, FormatSize(item.Bytes), item.Path); cleanup.Rows[i].Tag = item; }
                Notice.Text = "Unique stored file content: " + FormatSize(value.UniqueBytes) + ". Hard links counted once; allocation, compression and Recycle Bin excluded. Cleanup sizes are logical and may share content. Recycling does not free disk space until the Recycle Bin is emptied. Latest backups and individual history objects are protected. Removing a mirror requires downloading it again.";
            }
            else
            {
                foreach (CatalogBackup latest in value.Backups.FindAll(b => b.Latest))
                {
                    DateTime? successful = null; long bytes = 0;
                    foreach (CatalogBackup row in value.Backups.FindAll(b => b.History == latest.History))
                    {
                        bytes += row.TotalBytes;
                        if (row.CompletedUtc != null && (successful == null || row.CompletedUtc > successful)) successful = row.CompletedUtc;
                    }
                    string attention = latest.Status != Status.Complete ? "Needs attention" : successful == null || DateTime.UtcNow - successful.Value > TimeSpan.FromDays(7) ? "Overdue" : "Up to date";
                    if (latest.CheckResult.StartsWith("Problems found", StringComparison.Ordinal)) attention = "Check problems";
                    int i = grid.Rows.Add(latest.Profile, CatalogDate(successful), latest.Status, CatalogDate(latest.CheckedUtc), latest.CheckResult,
                        FormatSize(bytes), attention, latest.History); grid.Rows[i].Tag = latest;
                }
                Notice.Text = "Last check refers to the latest backup folder. Older backups remain available through Restore. Stored sizes are logical file sizes. Refresh after a backup or integrity check.";
            }
            if (value.Warnings.Count > 0) Notice.Text += "\r\nSkipped / unavailable: " + string.Join("; ", value.Warnings.ToArray());
            if (value.Backups.Count == 0) Notice.Text = "No backups found in the current or configured profile locations. " + Notice.Text;
        }
        static string CatalogDate(DateTime? date) { return date == null ? "—" : date.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
        internal List<StorageItem> SelectedCleanup()
        {
            cleanup.EndEdit(); var selected = new List<StorageItem>();
            foreach (DataGridViewRow row in cleanup.Rows) if (Equals(row.Cells[0].Value, true)) selected.Add((StorageItem)row.Tag);
            return selected;
        }
        void ReviewCleanup()
        {
            var selected = SelectedCleanup();
            if (selected.Count == 0) { Notice.Text = "Select items in the Cleanup tab first."; tabs.SelectedIndex = 1; return; }
            var lines = new List<string>(); long bytes = 0; var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (StorageItem item in selected) { lines.Add("Remove to Recycle Bin: " + item.Path); bytes += item.Bytes; sizes[item.Path] = item.Bytes; }
            using (var preview = new PreviewDialog(new OperationPlan { Label = "Move these selected items to the Recycle Bin? Mirrors will need to be downloaded again.", Actions = lines, Bytes = bytes, FileSizes = sizes, ShowDetails = true }))
                if (preview.ShowDialog(this) != DialogResult.OK) return;
            Work(() => CleanupStorage(catalog, selected), count => { Notice.Text = "Recycled " + count + " item(s)."; RefreshCatalog(); });
        }
    }
}
