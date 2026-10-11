using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    readonly TextBox fullBackupName = new TextBox { Name = "FullBackupName", AccessibleName = "Name for this full backup", MaxLength = 80, Dock = DockStyle.Fill };
    readonly ComboBox incrementalBackup = new ComboBox { Name = "IncrementalBackup", AccessibleName = "Full backup to update", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    Control backupSetField;
    Label backupSetCaption;
    SourceChoice backupSetSource;
    int backupSetRequest;
    bool loadingBackupSets;
    internal void SetBackupSetSource(SourceChoice choice) { backupSetSource = choice; RefreshBackupSets(); }

    sealed class BackupSetItem
    {
        internal string Path, Label;
        public override string ToString() { return Label; }
    }

    Control BuildBackupSetField()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0) };
        backupSetCaption = FieldLabel("Name for this backup (optional)");
        panel.Controls.Add(backupSetCaption);
        panel.Controls.Add(new FieldPanel(fullBackupName) { Dock = DockStyle.Fill, Margin = Padding.Empty });
        panel.Controls.Add(incrementalBackup);
        GuiTheme.StyleComboBox(incrementalBackup);
        incrementalBackup.DropDown += delegate { if (!loadingBackupSets) RefreshBackupSets(); };
        backupSetField = panel;
        return panel;
    }

    void RefreshBackupSetField()
    {
        bool full = Selected == GuiOperation.FullBackup, incremental = Selected == GuiOperation.IncrementalBackup;
        backupSetField.Visible = full || incremental;
        fullBackupName.Parent.Visible = full;
        incrementalBackup.Visible = incremental;
        backupSetCaption.Text = full ? "Name for this backup (optional)" : "Full backup to update";
        if (incremental && !runner.IsBusy) RefreshBackupSets();
    }

    async void RefreshBackupSets()
    {
        if (config == null || runner.IsBusy || IsDisposed) return;
        if (Selected != GuiOperation.IncrementalBackup) { ++backupSetRequest; loadingBackupSets = false; return; }
        int request = ++backupSetRequest;
        var selected = incrementalBackup.SelectedItem as BackupSetItem;
        string selectedPath = selected == null ? null : selected.Path;
        Config options;
        try { options = CaptureConfig(); }
        catch (Exception) { incrementalBackup.Items.Clear(); return; }
        SourceChoice choice = backupSetSource;
        if (options.SourceFolder.Length != 0 && (choice == null || string.IsNullOrEmpty(choice.Path) || !string.Equals(options.SourceFolder.TrimEnd('\\'), choice.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            choice = new SourceChoice { Path = options.SourceFolder };
        loadingBackupSets = true;
        incrementalBackup.Enabled = false;
        string message = "No full backups — choose Full backup";
        var items = new List<BackupSetItem>();
        try
        {
            if (choice == null) message = "Detect a device or choose a source first";
            else items = await Task.Run(delegate {
                using (new OperationContext(new GuiInteraction(this, value => { }), CancellationToken.None) { PreviewOnly = true }.Activate())
                {
                    PreparedSource prepared = Session.PrepareSource(options, choice);
                    var result = new List<BackupSetItem>();
                    var paths = Backups.ListBackups(prepared.History);
                    for (int i = paths.Count - 1; i >= 0; i--)
                        result.Add(new BackupSetItem { Path = paths[i], Label = BackupSetNames.Label(paths[i]) + "  [" + Workflow.BackupStatus(paths[i]) + "]" });
                    return result;
                }
            });
        }
        catch (Exception ex) { message = "Cannot list backups: " + ex.Message; }
        if (IsDisposed || request != backupSetRequest) return;
        loadingBackupSets = false;
        // Opening the dropdown can start a refresh while the user chooses another item.
        selected = incrementalBackup.SelectedItem as BackupSetItem;
        if (selected != null) selectedPath = selected.Path;
        incrementalBackup.Items.Clear();
        foreach (var item in items) incrementalBackup.Items.Add(item);
        if (items.Count == 0) incrementalBackup.Items.Add(message);
        int index = items.FindIndex(item => string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        incrementalBackup.SelectedIndex = index >= 0 ? index : selectedPath == null || items.Count == 0 ? 0 : -1;
        incrementalBackup.Enabled = !runner.IsBusy && items.Count != 0;
    }
}
