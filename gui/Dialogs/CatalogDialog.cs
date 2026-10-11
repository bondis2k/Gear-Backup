using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using static StorageCatalog;
using static PathSafety;
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
    Button viewRecipes, renameHistory, previewCleanup;
    readonly Label cleanupSelection = GuiTheme.Label("", 10, GuiTheme.Ink);
    // Device filter on both windows: "All devices", then each device that has histories (or, on Storage,
    // an MTP mirror), and "Unknown device" for histories made before backups recorded their device.
    readonly ComboBox deviceFilter = new ComboBox { AccessibleName = "Device", DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    const string AllDevices = "All devices", UnknownDevice = "Unknown device";
    bool fillingDevices;
    // The device detected in the main window, chosen in the filter when the window first fills (if it
    // has anything to show); after that the filter keeps whatever the user picks.
    string startDevice;

    // `initial` shows an existing scan (tests); otherwise the window scans when it opens. `device` is the
    // device detected in the main window, if any, which the filter starts on.
    public CatalogDialog(MainForm owner, Config options, bool storageMode, BackupCatalog initial = null, DeviceProfile device = null)
        : base(owner, storageMode ? "Storage" : "Dashboard", storageMode
            ? "See what uses space and review cleanup before recycling items."
            : "Backup health across your devices and profiles. Backups older than 7 days are marked overdue.")
    {
        config = options;
        storage = storageMode;
        startDevice = device == null ? null : device.Name;
        grid = ToolGrid(storage ? "StorageTable" : "DashboardTable");
        deviceFilter.Name = storage ? "StorageDevice" : "DashboardDevice";
        GuiTheme.StyleComboBox(deviceFilter);
        deviceFilter.Items.Add(AllDevices);
        deviceFilter.SelectedIndex = 0;
        deviceFilter.SelectedIndexChanged += delegate { if (!fillingDevices && catalog != null) Populate(catalog); };
        if (storage) BuildStorage(owner);
        else BuildDashboard(owner);
        ToolButton("Refresh", "RefreshCatalog", RefreshCatalog);
        // Opens the selected backup's folder, or on Storage's Cleanup tab the selected item's folder.
        ToolButton("Open folder", "OpenCatalogFolder", delegate {
            string folder = null;
            if (storage && tabs.SelectedIndex == 1)
            {
                var item = cleanup.CurrentRow == null ? null : cleanup.CurrentRow.Tag as StorageItem;
                if (item != null) folder = item.Folder ? item.Path : Path.GetDirectoryName(item.Path);
            }
            else
            {
                CatalogBackup row = SelectedBackup();
                if (row != null) folder = row.Path;
            }
            if (folder != null && Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        });
        if (initial != null) Populate(initial);
        else Shown += delegate { RefreshCatalog(); };
    }

    void BuildStorage(MainForm owner)
    {
        foreach (string column in new[] { "Profile / backup", "Current files", "Old versions", "Content store / history", "Metadata", "Unique content", "Location" })
            grid.Columns.Add(column, column);
        var usage = new TabPage("Space usage");
        var clean = new TabPage("Cleanup");
        usage.Controls.Add(grid);
        clean.Controls.Add(BuildCleanupPage());
        tabs.TabPages.Add(usage);
        tabs.TabPages.Add(clean);
        SetContent(WithDeviceFilter(GuiTheme.Tabbed(tabs)));
        previewCleanup = ToolButton("Preview cleanup", "PreviewCleanup", ReviewCleanup, true);
        AddRecipesButton(owner);
        // View recipes and Rename work on Space usage; Open folder on whichever tab is showing.
        tabs.SelectedIndexChanged += delegate { UpdateRecipesButton(); };
        UpdateCleanupSelection();
    }

    // The Cleanup tab: what can go, in plain words, what removing it means, and what's selected so far.
    // Only the Remove column can be changed; nothing is selected at first.
    Control BuildCleanupPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label intro = GuiTheme.Label("Move items you no longer need to the Recycle Bin to free space. Nothing is removed until you review " +
            "and confirm, and the latest backup of each history is always kept.", 9, GuiTheme.Muted);
        intro.Name = "CleanupIntro";
        intro.Margin = new Padding(4, 6, 4, 8);
        page.SizeChanged += delegate { intro.MaximumSize = new System.Drawing.Size(Math.Max(100, page.ClientSize.Width - 8), 0); };
        page.Controls.Add(intro, 0, 0);
        cleanup.ReadOnly = false;
        cleanup.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Remove", FillWeight = 24, MinimumWidth = 92 });
        foreach (string column in new[] { "Item", "Size", "If removed", "Location" })
        {
            int i = cleanup.Columns.Add(column, column);
            cleanup.Columns[i].ReadOnly = true;
        }
        cleanup.Columns["Item"].FillWeight = 220;
        cleanup.Columns["Size"].FillWeight = 45;
        cleanup.Columns["If removed"].FillWeight = 130;
        cleanup.Columns["Location"].FillWeight = 110;
        // Long item names wrap instead of being cut off; the row grows to fit.
        cleanup.Columns["Item"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        // Ticking a box takes effect at once, so the selection total and Preview cleanup follow it.
        cleanup.CurrentCellDirtyStateChanged += delegate { if (cleanup.IsCurrentCellDirty) cleanup.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        cleanup.CellValueChanged += delegate { UpdateCleanupSelection(); };
        cleanup.Dock = DockStyle.Fill;
        page.Controls.Add(cleanup, 0, 1);
        cleanupSelection.Name = "CleanupSelection";
        cleanupSelection.Margin = new Padding(4, 8, 4, 0);
        page.Controls.Add(cleanupSelection, 0, 2);
        return page;
    }

    void UpdateCleanupSelection()
    {
        if (!storage) return;
        List<StorageItem> selected = SelectedCleanup();
        long bytes = 0;
        foreach (StorageItem item in selected) bytes += item.Bytes;
        cleanupSelection.Text = selected.Count == 0 ? "Nothing selected. Tick the items you want to remove."
            : selected.Count + (selected.Count == 1 ? " item" : " items") + " selected · " + FormatSize(bytes);
        previewCleanup.Enabled = selected.Count > 0;
    }

    // A cleanup item in plain words, with the history it belongs to, and what removing it means.
    string CleanupItemText(StorageItem item)
    {
        if (item.Kind == StorageKind.Mirror)
            return "Downloaded device copy · " + HistoryNames.AutomaticName(Path.GetFileName(item.Path.TrimEnd('\\')));
        string history = HistoryLabel(item.History);
        if (item.Kind == StorageKind.OlderBackup) return "Older backup · " + history + " · " + Path.GetFileName(item.Path.TrimEnd('\\'));
        if (item.Kind == StorageKind.Zip) return "ZIP copy · " + history + " · " + Path.GetFileNameWithoutExtension(item.Path);
        if (item.Kind == StorageKind.OldVersion && item.Backup != null && item.Path.StartsWith(Rooted(item.Backup), StringComparison.OrdinalIgnoreCase))
            return "Old version of a file · " + history + " · " + item.Path.Substring(Rooted(item.Backup).Length);
        return item.Kind + " · " + history;
    }

    // What removing an item means: short for the table, with the full explanation as its tooltip.
    static string CleanupEffect(StorageItem item, bool full = false)
    {
        if (item.Kind == StorageKind.Mirror)
            return full ? "A temporary copy of the device made before each backup. Your backups are not affected; it is downloaded again at the next backup."
                : "Downloaded again next backup";
        if (item.Kind == StorageKind.OlderBackup)
            return full ? "This backup date can no longer be restored. Newer backups of this history, including the latest, are kept."
                : "Can't restore this date anymore";
        if (item.Kind == StorageKind.Zip)
            return full ? "Only the extra ZIP file is removed; the backup folder it was made from stays." : "Backup folder stays";
        return full ? "This earlier version of the file can no longer be restored; the current file stays." : "Can't restore this version anymore";
    }

    // A history's name as the lists show it (its nickname or readable name).
    string HistoryLabel(string history)
    {
        CatalogBackup row = catalog == null ? null : catalog.Backups.Find(b => string.Equals(b.History, history, StringComparison.OrdinalIgnoreCase));
        return row != null ? row.Profile : HistoryNames.DisplayName(history);
    }

    void BuildDashboard(MainForm owner)
    {
        foreach (string column in new[] { "Profile", "Last successful backup", "Latest status", "Last check", "Check result", "Stored size", "Attention", "Location" })
            grid.Columns.Add(column, column);
        SetContent(WithDeviceFilter(grid));
        ToolButton("Check latest backup", "CheckCatalogBackup", delegate {
            CatalogBackup row = SelectedBackup();
            if (row == null) return;
            Work(() => { using (LockHistory(row.History)) return new IntegrityEngine().Run(new List<string> { row.Path }, OperationContext.Current); },
                result => { Notice.Text = result.Summary; RefreshCatalog(); });
        }, true);
        AddRecipesButton(owner);
    }

    // View recipes, on both windows: opens the selected backup's saved X100VI recipes. It is greyed out
    // unless the selected row is a backup folder holding recipe exports (a settings backup).
    void AddRecipesButton(MainForm owner)
    {
        viewRecipes = ToolButton("View recipes", "ViewCatalogRecipes", delegate {
            CatalogBackup row = SelectedBackup();
            if (row == null || !RecipeViewerDialog.HasRecipes(row.Path)) return;
            using (var viewer = new RecipeViewerDialog(owner, row.Path)) viewer.ShowDialog(this);
        });
        renameHistory = ToolButton("Rename…", "RenameHistory", RenameHistory);
        grid.CurrentCellChanged += delegate { UpdateRecipesButton(); };
        UpdateRecipesButton();
    }

    // Also enables Rename for a selected profile history. On Storage
    // both act on Space usage, so they are greyed out while Cleanup is showing.
    void UpdateRecipesButton()
    {
        CatalogBackup row = storage && tabs.SelectedIndex == 1 ? null : SelectedBackup();
        viewRecipes.Enabled = row != null && RecipeViewerDialog.HasRecipes(row.Path);
        renameHistory.Enabled = row != null && Renamable(row);
    }

    static bool Renamable(CatalogBackup row)
    {
        return Path.GetFileName(Path.GetDirectoryName(row.History.TrimEnd('\\'))).Equals("profiles", StringComparison.OrdinalIgnoreCase);
    }

    // Gives the selected backup's history a nickname, shown in every list instead of its automatic name.
    // The folder on disk keeps its name; an empty nickname goes back to the automatic name.
    void RenameHistory()
    {
        CatalogBackup row = SelectedBackup();
        if (row == null || !Renamable(row)) return;
        string automatic = HistoryNames.AutomaticName(Path.GetFileName(row.History.TrimEnd('\\')));
        using (var dialog = new SearchDialog("Rename backup history",
            "Shown instead of “" + automatic + "” in every list. Leave empty to use that name again; the folder on disk keeps its name."))
        {
            dialog.Input.Name = "HistoryNickname";
            dialog.Input.Text = HistoryNames.Nickname(row.History) ?? "";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            HistoryNames.SetNickname(row.History, dialog.Input.Text);
        }
        string name = HistoryNames.DisplayName(row.History);
        foreach (CatalogBackup backup in catalog.Backups)
            if (string.Equals(backup.History, row.History, StringComparison.OrdinalIgnoreCase)) backup.Profile = name;
        Populate(catalog);
        Notice.Text = "Renamed to “" + name + "”.";
    }

    // The window's content with the Device filter above it.
    Control WithDeviceFilter(Control view)
    {
        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        Label label = GuiTheme.Label("Device", 10, GuiTheme.Ink);
        label.Margin = new Padding(0, 6, 8, 0);
        filterRow.Controls.Add(label);
        filterRow.Controls.Add(deviceFilter);
        var content = new TableLayoutPanel { RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(filterRow, 0, 0);
        view.Dock = DockStyle.Fill;
        content.Controls.Add(view, 0, 1);
        return content;
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
        UpdateRecipesButton();
    }

    // Space per backup and the items cleanup can recycle, for the device chosen in the filter. A backup,
    // its old versions and its history's ZIPs belong to the history's device (as on the Dashboard); an
    // MTP mirror to the device its folder is named after. Changing the filter rebuilds the Cleanup list,
    // so nothing hidden from view stays selected.
    void PopulateStorage(BackupCatalog value)
    {
        Dictionary<string, DeviceProfile> historyDevices = HistoryDevices(value);
        var devices = new List<DeviceProfile>(historyDevices.Values);
        foreach (StorageItem item in value.Cleanup)
            if (item.Kind == StorageKind.Mirror) devices.Add(MirrorDevice(item.Path));
        FillDeviceChoices(devices);
        Func<StorageItem, DeviceProfile> itemDevice = item => {
            if (item.Kind == StorageKind.Mirror) return MirrorDevice(item.Path);
            DeviceProfile device;
            return historyDevices.TryGetValue(item.History, out device) ? device : null;
        };
        foreach (CatalogBackup row in value.Backups)
        {
            DeviceProfile device;
            if (!VisibleFor(historyDevices.TryGetValue(row.History, out device) ? device : null)) continue;
            int i = grid.Rows.Add(row.Profile + " / " + Path.GetFileName(row.Path), FormatSize(row.FilesBytes), FormatSize(row.VersionsBytes),
                FormatSize(row.ObjectsBytes), FormatSize(row.MetadataBytes), FormatSize(row.UniqueBytes), row.Path);
            grid.Rows[i].Tag = row;
        }
        long zipBytes = 0, mirrorBytes = 0;
        foreach (StorageItem item in value.Cleanup)
        {
            if (!VisibleFor(itemDevice(item))) continue;
            if (item.Kind == StorageKind.Zip) zipBytes += item.Bytes;
            if (item.Kind == StorageKind.Mirror) mirrorBytes += item.Bytes;
            int i = cleanup.Rows.Add(false, CleanupItemText(item), FormatSize(item.Bytes), CleanupEffect(item), item.Path);
            cleanup.Rows[i].Tag = item;
            cleanup.Rows[i].Cells["If removed"].ToolTipText = CleanupEffect(item, true);
            cleanup.Rows[i].Cells["Location"].ToolTipText = item.Path;
        }
        grid.Rows.Add("ZIP archives", FormatSize(zipBytes));
        grid.Rows.Add("Downloaded device copies", FormatSize(mirrorBytes));
        UpdateCleanupSelection();
        bool filtered = (deviceFilter.SelectedItem as string ?? AllDevices) != AllDevices;
        Notice.Text = "Backups use " + FormatSize(value.UniqueBytes) + (filtered ? " in total across all devices" : " in total") +
            " (files kept more than once through hard links are counted once). Space is freed when you empty the Recycle Bin.";
    }

    // Each history's device: its latest backup's, as on the Dashboard.
    static Dictionary<string, DeviceProfile> HistoryDevices(BackupCatalog value)
    {
        var devices = new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogBackup row in value.Backups)
            if (row.Latest) devices[row.History] = row.Device;
        return devices;
    }

    // The device an MTP mirror belongs to, from its folder name ("x100vi-<serial>"); null if none matches.
    static DeviceProfile MirrorDevice(string mirror)
    {
        string name = Path.GetFileName(mirror.TrimEnd('\\'));
        return Array.Find(Devices.All, d => name.StartsWith(d.Id + "-", StringComparison.OrdinalIgnoreCase));
    }

    // One row per history, describing its latest backup and when the history last succeeded. Only
    // histories of the device chosen in the filter are listed; a history's device is its latest backup's.
    void PopulateDashboard(BackupCatalog value)
    {
        FillDeviceChoices(value.Backups.FindAll(b => b.Latest).ConvertAll(b => b.Device));
        foreach (CatalogBackup latest in value.Backups.FindAll(b => b.Latest && VisibleFor(b.Device)))
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

    // The filter's choices: the devices that appear in this scan (null for an unknown device), in the
    // usual device order. The current choice is kept when it still exists.
    void FillDeviceChoices(List<DeviceProfile> present)
    {
        string selected = deviceFilter.SelectedItem as string ?? AllDevices;
        var names = new List<string> { AllDevices };
        foreach (DeviceProfile device in Devices.All)
            if (present.Contains(device)) names.Add(device.Name);
        if (present.Contains(null)) names.Add(UnknownDevice);
        if (startDevice != null)
        {
            if (names.Contains(startDevice)) selected = startDevice;
            startDevice = null;
        }
        fillingDevices = true;
        try
        {
            deviceFilter.Items.Clear();
            deviceFilter.Items.AddRange(names.ToArray());
            deviceFilter.SelectedIndex = Math.Max(0, names.IndexOf(selected));
        }
        finally { fillingDevices = false; }
    }

    // Whether something of this device (null: unknown) is shown under the current filter.
    bool VisibleFor(DeviceProfile device)
    {
        string selected = deviceFilter.SelectedItem as string ?? AllDevices;
        if (selected == AllDevices) return true;
        return device == null ? selected == UnknownDevice : device.Name == selected;
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
        var plan = new OperationPlan { Label = "Move these items to the Recycle Bin? Downloaded device copies are downloaded again at the next backup.",
            Actions = lines, Bytes = bytes, FileSizes = sizes, ShowDetails = true };
        using (var preview = new PreviewDialog(plan))
            if (preview.ShowDialog(this) != DialogResult.OK) return;
        Work(() => CleanupStorage(catalog, selected), count => { Notice.Text = "Recycled " + count + " item(s)."; RefreshCatalog(); });
    }
}
