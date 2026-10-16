using System;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // The window's side of the sections that follow the selected operation: what they ask of the window.
    internal sealed partial class MainForm : IOperationSectionHost
    {
        readonly BackupSetsSection backupSets;

        // The detected device's source, so the incremental list does not need the typed source folder.
        internal void SetBackupSetSource(SourceChoice choice) { backupSets.SetSource(choice); }

        GuiOperation IOperationSectionHost.SelectedOperation { get { return Selected; } }
        bool IOperationSectionHost.HasOptions { get { return config != null; } }
        Config IOperationSectionHost.CaptureOptions() { return CaptureConfig(); }
        IOperationInteraction IOperationSectionHost.QuietInteraction() { return new GuiInteraction(this, p => { }); }
    }
}
