using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using static StorageCatalog;
using static ConsoleUi;
using static Workflow;

// The Dashboard (one row per history: last success, status, last check) and Storage (space per backup,
// and items to recycle) windows. Both show a BackupCatalog; Refresh scans again in the background.
internal sealed class CatalogDialog : ToolsDialog
{
    readonly Config config;
    readonly bool storage;
    readonly DataGridView grid;
    readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
    readonly DataGridView cleanup = ToolGrid("CleanupItems");
    BackupCatalog catalog;
    // Dashboard filter: "All devices", then each device that has histories, and "Unknown device" for
    // histories made before backups recorded their device.
    readonly ComboBox deviceFilter = new ComboBox { Name = "DashboardDevice", AccessibleName = "Device",
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    const string AllDevices = "All devices", UnknownDevice = "Unknown device";
    bool fillingDevices;

    // `initial` shows an existing scan (tests); otherwise the window scans when it opens.
    public CatalogDialog(MainForm owner, Config options, bool storageMode, BackupCatalog initial = null)
        : base(owner, storageMode ? "Storage" : "Dashboard", storageMode
            ? "See what uses space and review cleanup before recycling items."
            : "Backup health across your devices and profiles. Backups older than 7 days are marked overdue.")
    {
        config = options;
        storage = storageMode;
        grid = ToolGrid(storage ? "StorageTable" : "DashboardTable");
        if (storage) BuildStorage();
        else BuildDashboard(owner);
        ToolButton("Refresh", "RefreshCatalog", RefreshCatalog);
        ToolButton("Open folder", "OpenCatalogFolder", delegate {
            CatalogBackup row = SelectedBackup();
            if (row != null && Directory.Exists(row.Path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Path) { UseShellExecute = true });
        });
        if (initial != null) Populate(initial);
        else Shown += delegate { RefreshCatalog(); };
    }

    void BuildStorage()
    {
        foreach (string column in new[] { "Profile / backup", "Current files", "Old versions", "History objects", "Metadata", "Unique content", "Location" })
            grid.Columns.Add(column, column);
        var usage = new TabPage("Space usage");
        var clean = new TabPage("Cleanup");
        usage.Controls.Add(grid);
        clean.Controls.Add(cleanup);
        tabs.TabPages.Add(usage);
        tabs.TabPages.Add(clean);
        SetContent(GuiTheme.Tabbed(tabs));
        // Only the Select column can be changed; nothing is selected at first.
        cleanup.ReadOnly = false;
        cleanup.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Select", FillWeight = 15 });
        foreach (string column in new[] { "Kind", "Size", "Location" })
        {
            int i = cleanup.Columns.Add(column, column);
            cleanup.Columns[i].ReadOnly = true;
        }
        cleanup.Columns["Location"].FillWeight = 280;
        ToolButton("Preview cleanup", "PreviewCleanup", ReviewCleanup, true);
    }

    void BuildDashboard(MainForm owner)
    {
        foreach (string column in new[] { "Profile", "Last successful backup", "Latest status", "Last check", "Check result", "Stored size", "Attention", "Location" })
            grid.Columns.Add(column, column);
        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        Label label = GuiTheme.Label("Device", 10, GuiTheme.Ink);
        label.Margin = new Padding(0, 6, 8, 0);
        GuiTheme.StyleComboBox(deviceFilter);
        deviceFilter.Items.Add(AllDevices);
        deviceFilter.SelectedIndex = 0;
        deviceFilter.SelectedIndexChanged += delegate { if (!fillingDevices && catalog != null) Populate(catalog); };
        filterRow.Controls.Add(label);
        filterRow.Controls.Add(deviceFilter);
        var content = new TableLayoutPanel { RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(filterRow, 0, 0);
        content.Controls.Add(grid, 0, 1);
        SetContent(content);
        ToolButton("Check latest backup", "CheckCatalogBackup", delegate {
            CatalogBackup row = SelectedBackup();
            if (row == null) return;
            Work(() => { using (LockHistory(row.History)) return new IntegrityEngine().Run(new List<string> { row.Path }, OperationContext.Current); },
                result => { Notice.Text = result.Summary; RefreshCatalog(); });
        }, true);
        ToolButton("View recipes", "ViewCatalogRecipes", delegate {
            CatalogBackup row = SelectedBackup();
            if (row == null) return;
            using (var viewer = new RecipeViewerDialog(owner, row.Path)) viewer.ShowDialog(this);
        });
    }

    // The backup of the selected row; null for summary rows (ZIPs, mirrors) or no selection.
    CatalogBackup SelectedBackup()
    {
        return grid.CurrentRow == null ? null : grid.CurrentRow.Tag as CatalogBackup;
    }

    void RefreshCatalog() { Work(() => storage ? ScanBackupCatalog(config) : ScanDashboardCatalog(config), Populate); }

    internal void Populate(BackupCatalog value)
    {
        catalog = value;
        grid.Rows.Clear();
        cleanup.Rows.Clear();
        if (storage) PopulateStorage(value);
        else PopulateDashboard(value);
        if (value.Warnings.Count > 0) Notice.Text += "\r\nSkipped / unavailable: " + string.Join("; ", value.Warnings.ToArray());
        if (value.Backups.Count == 0) Notice.Text = "No backups found in the current or configured profile locations. " + Notice.Text;
    }

    void PopulateStorage(BackupCatalog value)
    {
        foreach (CatalogBackup row in value.Backups)
        {
            int i = grid.Rows.Add(row.Profile + " / " + Path.GetFileName(row.Path), FormatSize(row.FilesBytes), FormatSize(row.VersionsBytes),
                FormatSize(row.ObjectsBytes), FormatSize(row.MetadataBytes), FormatSize(row.UniqueBytes), row.Path);
            grid.Rows[i].Tag = row;
        }
        grid.Rows.Add("ZIP archives", FormatSize(value.ZipBytes));
        grid.Rows.Add("MTP mirrors", FormatSize(value.MirrorBytes));
        foreach (StorageItem item in value.Cleanup)
        {
            int i = cleanup.Rows.Add(false, item.Kind, FormatSize(item.Bytes), item.Path);
            cleanup.Rows[i].Tag = item;
        }
        Notice.Text = "Unique stored file content: " + FormatSize(value.UniqueBytes) + ". Hard links counted once; allocation, compression and " +
            "Recycle Bin excluded. Cleanup sizes are logical and may share content. Recycling does not free disk space until the Recycle Bin " +
            "is emptied. Latest backups and individual history objects are protected. Removing a mirror requires downloading it again.";
    }

    // One row per history, describing its latest backup and when the history last succeeded. Only
    // histories of the device chosen in the filter are listed; a history's device is its latest backup's.
    void PopulateDashboard(BackupCatalog value)
    {
        FillDeviceChoices(value);
        foreach (CatalogBackup latest in value.Backups.FindAll(b => b.Latest && ShownForDevice(b)))
        {
            DateTime? successful = null;
            long bytes = 0;
            foreach (CatalogBackup row in value.Backups.FindAll(b => b.History == latest.History))
            {
                bytes += row.TotalBytes;
                if (row.CompletedUtc != null && (successful == null || row.CompletedUtc > successful)) successful = row.CompletedUtc;
            }
            string attention = latest.Status != Status.Complete ? "Needs attention"
                : successful == null || DateTime.UtcNow - successful.Value > TimeSpan.FromDays(7) ? "Overdue" : "Up to date";
            if (latest.CheckResult.StartsWith("Problems found", StringComparison.Ordinal)) attention = "Check problems";
            int i = grid.Rows.Add(latest.Profile, CatalogDate(successful), latest.Status, CatalogDate(latest.CheckedUtc), latest.CheckResult,
                value.StorageDetails ? FormatSize(bytes) : "See Storage", attention, latest.History);
            grid.Rows[i].Tag = latest;
        }
        Notice.Text = "Last check refers to the latest backup folder. Older backups remain available through Restore. " +
            "Open Storage for detailed sizes. Refresh after a backup or integrity check.";
    }

    // The devices that have histories in this scan. The current choice is kept when it still exists.
    void FillDeviceChoices(BackupCatalog value)
    {
        string selected = deviceFilter.SelectedItem as string ?? AllDevices;
        var names = new List<string> { AllDevices };
        foreach (DeviceProfile device in Devices.All)
            if (value.Backups.Exists(b => b.Latest && b.Device == device)) names.Add(device.Name);
        if (value.Backups.Exists(b => b.Latest && b.Device == null)) names.Add(UnknownDevice);
        fillingDevices = true;
        try
        {
            deviceFilter.Items.Clear();
            deviceFilter.Items.AddRange(names.ToArray());
            deviceFilter.SelectedIndex = Math.Max(0, names.IndexOf(selected));
        }
        finally { fillingDevices = false; }
    }

    bool ShownForDevice(CatalogBackup latest)
    {
        string selected = deviceFilter.SelectedItem as string ?? AllDevices;
        if (selected == AllDevices) return true;
        return latest.Device == null ? selected == UnknownDevice : latest.Device.Name == selected;
    }

    static string CatalogDate(DateTime? date) { return date == null ? "—" : date.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }

    internal List<StorageItem> SelectedCleanup()
    {
        cleanup.EndEdit();
        var selected = new List<StorageItem>();
        foreach (DataGridViewRow row in cleanup.Rows)
            if (Equals(row.Cells[0].Value, true)) selected.Add((StorageItem)row.Tag);
        return selected;
    }

    // Shows what would be recycled, and recycles it after Proceed.
    void ReviewCleanup()
    {
        var selected = SelectedCleanup();
        if (selected.Count == 0)
        {
            Notice.Text = "Select items in the Cleanup tab first.";
            tabs.SelectedIndex = 1;
            return;
        }
        var lines = new List<string>();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (StorageItem item in selected)
        {
            lines.Add("Remove to Recycle Bin: " + item.Path);
            bytes += item.Bytes;
            sizes[item.Path] = item.Bytes;
        }
        var plan = new OperationPlan { Label = "Move these selected items to the Recycle Bin? Mirrors will need to be downloaded again.",
            Actions = lines, Bytes = bytes, FileSizes = sizes, ShowDetails = true };
        using (var preview = new PreviewDialog(plan))
            if (preview.ShowDialog(this) != DialogResult.OK) return;
        Work(() => CleanupStorage(catalog, selected), count => { Notice.Text = "Recycled " + count + " item(s)."; RefreshCatalog(); });
    }
}
