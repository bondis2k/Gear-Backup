using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // The naming and set-choosing field of the Backup tab: a name for a full backup (or the ZIP file), or the full backup an
    // incremental run updates, listed from the history in the background.
    internal sealed class BackupSetsSection
    {
        readonly IOperationSectionHost host;
        readonly TextBox fullName = new TextBox { Name = "FullBackupName", AccessibleName = "Name for this full backup", MaxLength = 80, Dock = DockStyle.Fill };
        readonly TextBox incrementalName = new TextBox { Name = "IncrementalBackupName", AccessibleName = "Name for this incremental backup", MaxLength = 80, Dock = DockStyle.Fill };
        readonly ComboBox incrementalBackup = new ComboBox { Name = "IncrementalBackup", AccessibleName = "Full backup to update", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        Control field;
        Label caption, incrementalCaption;
        SourceChoice source;
        int request;
        bool loading, closed;

        internal BackupSetsSection(IOperationSectionHost host) { this.host = host; }

        sealed class Item
        {
            internal string Path, Label;
            public override string ToString() { return Label; }
        }

        internal string FullName { get { return fullName.Text; } }
        internal string IncrementalName { get { return incrementalName.Text; } }
        // The full backup chosen for an incremental run, or null when none is chosen yet or the list is still loading.
        internal string SelectedBackup
        {
            get
            {
                var item = incrementalBackup.SelectedItem as Item;
                return loading || item == null ? null : item.Path;
            }
        }

        // The detected device's source, so the list does not need the typed source folder.
        internal void SetSource(SourceChoice choice) { source = choice; Refresh(); }
        internal void ClearSource() { source = null; }
        internal void SetEnabled(bool enabled) { field.Enabled = enabled; }
        internal void Close() { closed = true; }

        internal Control Build()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0) };
            caption = GuiTheme.Caption("Name for this backup (optional)");
            panel.Controls.Add(caption);
            panel.Controls.Add(new FieldPanel(fullName) { Dock = DockStyle.Fill, Margin = Padding.Empty });
            panel.Controls.Add(incrementalBackup);
            incrementalCaption = GuiTheme.Caption("Name for this incremental backup (optional)");
            incrementalCaption.Margin = new Padding(0, 10, 0, 0);
            panel.Controls.Add(incrementalCaption);
            panel.Controls.Add(new FieldPanel(incrementalName) { Dock = DockStyle.Fill, Margin = Padding.Empty });
            GuiTheme.StyleComboBox(incrementalBackup);
            incrementalBackup.DropDown += delegate { if (!loading) Refresh(); };
            field = panel;
            return panel;
        }

        // Shows the controls that belong to the selected operation.
        internal void RefreshField()
        {
            GuiOperation selected = host.SelectedOperation;
            bool archive = selected == GuiOperation.ZipArchive;
            bool full = selected == GuiOperation.FullBackup || archive, incremental = selected == GuiOperation.IncrementalBackup;
            field.Visible = full || incremental;
            fullName.Parent.Visible = full;
            incrementalBackup.Visible = incremental;
            incrementalCaption.Visible = incrementalName.Parent.Visible = incremental;
            caption.Text = archive ? "ZIP filename (optional)" : full ? "Name for this backup (optional)" : "Full backup to update";
            if (incremental && !host.Busy) Refresh();
        }

        // Lists the history's full backups for the incremental choice, keeping the user's selection.
        internal async void Refresh()
        {
            if (!host.HasOptions || host.Busy || closed) return;
            if (host.SelectedOperation != GuiOperation.IncrementalBackup) { ++request; loading = false; return; }
            int current = ++request;
            var selected = incrementalBackup.SelectedItem as Item;
            string selectedPath = selected == null ? null : selected.Path;
            Config options;
            try { options = host.CaptureOptions(); }
            catch (Exception ex) when (ExpectedFailure.Is(ex)) { incrementalBackup.Items.Clear(); return; }
            SourceChoice choice = source;
            if (options.SourceFolder.Length != 0 && (choice == null || string.IsNullOrEmpty(choice.Path) || !string.Equals(options.SourceFolder.TrimEnd('\\'), choice.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                choice = new SourceChoice { Path = options.SourceFolder };
            loading = true;
            incrementalBackup.Enabled = false;
            string message = "No full backups — choose Full backup";
            var items = new List<Item>();
            try
            {
                if (choice == null) message = "Detect a device or choose a source first";
                else items = await Task.Run(delegate {
                    using (new OperationContext(host.QuietInteraction(), CancellationToken.None) { PreviewOnly = true }.Activate())
                    {
                        PreparedSource prepared = Session.PrepareSource(options, choice);
                        var result = new List<Item>();
                        var paths = BackupFolders.ListBackups(prepared.History);
                        for (int i = paths.Count - 1; i >= 0; i--)
                            result.Add(new Item { Path = paths[i], Label = BackupSetNames.Label(paths[i]) + "  [" + BackupStatuses.BackupStatus(paths[i]) + "]" });
                        return result;
                    }
                });
            }
            catch (Exception ex) { message = "Cannot list backups: " + ex.Message; } // shown in the list itself
            if (closed || current != request) return;
            loading = false;
            // Opening the dropdown can start a refresh while the user chooses another item.
            selected = incrementalBackup.SelectedItem as Item;
            if (selected != null) selectedPath = selected.Path;
            incrementalBackup.Items.Clear();
            foreach (var item in items) incrementalBackup.Items.Add(item);
            if (items.Count == 0) incrementalBackup.Items.Add(message);
            int index = items.FindIndex(item => string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            incrementalBackup.SelectedIndex = index >= 0 ? index : selectedPath == null || items.Count == 0 ? 0 : -1;
            incrementalBackup.Enabled = !host.Busy && items.Count != 0;
        }
    }
}
