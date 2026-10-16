using System;
using System.IO;
using static GearBackup.AppSettings;
using static GearBackup.PathSafety;
using static GearBackup.ProfileNaming;

namespace GearBackup.Gui
{
    // What the window's fields currently say, gathered so the rules for turning them into settings can run (and be
    // tested) without a window.
    internal sealed class WindowInputs
    {
        public GuiOperation Operation;
        public string BackupFolder = "", Source = "", RestoreDestination = "", Profile = "";
        public bool ZipChecked;
        public string FullName = "", IncrementalName = "";
        // The full backup chosen to update, or null when none is chosen yet.
        public string ChosenBackup;
        // The restore chosen in the Restore area; called only when Restore is the operation.
        public Func<RestoreRequest> RestoreSelection;
        public bool ArchiveReady;
        public string ArchivePath = "";
    }

    static class WindowOptions
    {
        internal static bool Restoring(GuiOperation operation) { return operation == GuiOperation.Restore || operation == GuiOperation.RestoreZip; }
        internal static bool ZipApplies(GuiOperation operation) { return operation == GuiOperation.FullBackup || operation == GuiOperation.CameraSettings; }

        // The settings an operation runs with: every saved setting, with what the window shows laid over it. With
        // forOperation, the choices only that operation needs are checked and included, and a missing one is refused.
        internal static Config Capture(Config saved, WindowInputs window, bool forOperation)
        {
            // Start from every saved setting, so settings without a field in this window are kept.
            Config copy = saved.Clone();
            GuiOperation operation = window.Operation;
            copy.BackupFolder = string.IsNullOrWhiteSpace(window.BackupFolder) ? DefaultBackupFolder() : ExpandPath(window.BackupFolder);
            copy.SourceFolder = forOperation && (operation == GuiOperation.Check || operation == GuiOperation.CameraSettings) ? "" :
                ExpandPath(forOperation && Restoring(operation) ? window.RestoreDestination : window.Source);
            copy.Profile = (window.Profile ?? "").Trim();
            copy.Zip = window.ZipChecked && ZipApplies(operation);
            if (copy.Profile.Length > 0) ValidateProfile(copy.Profile);
            if (!forOperation) return copy;
            if (operation == GuiOperation.Restore) copy.RestoreSelection = window.RestoreSelection();
            if (operation == GuiOperation.RestoreZip)
            {
                if (!window.ArchiveReady) throw new IOException("Choose a valid ZIP archive and detect a matching destination device.");
                copy.RestoreArchive = window.ArchivePath;
            }
            if (operation == GuiOperation.FullBackup || operation == GuiOperation.ZipArchive) copy.FullBackupName = BackupSetNames.Validate(window.FullName);
            if (operation == GuiOperation.IncrementalBackup)
            {
                copy.IncrementalBackupName = BackupSetNames.Validate(window.IncrementalName);
                if (window.ChosenBackup == null) throw new IOException("Choose a full backup to update. Detect the device or choose a source first.");
                copy.IncrementalBackup = window.ChosenBackup;
            }
            return copy;
        }
    }
}
