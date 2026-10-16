using System;
using System.IO;
using GearBackup;
using GearBackup.Gui;

// The rules that turn the window's fields into the settings an operation runs with, without a window.
partial class Tests
{
    [Test("Window options", Gui = true)]
    static void TestWindowOptions()
    {
        var saved = new Config { BackupFolder = @"C:\Saved", SourceFolder = @"C:\OldSource", Profile = "Old", Verify = true, Thorough = true };
        var inputs = new WindowInputs { Operation = GuiOperation.FullBackup, BackupFolder = @"  D:\Backups ", Source = @"E:\", Profile = " Studio ", ZipChecked = true, FullName = " Live set " };
        Config backup = WindowOptions.Capture(saved, inputs, true);
        Assert(backup.BackupFolder == @"D:\Backups" && backup.SourceFolder == @"E:\" && backup.Profile == "Studio" && backup.Zip && backup.FullBackupName == "Live set",
            "a full backup takes the window's folders, profile, zip choice and name: " + backup.BackupFolder + "|" + backup.SourceFolder + "|" + backup.FullBackupName);
        Assert(backup.Verify && backup.Thorough, "settings without a field in the window are kept");
        Assert(saved.BackupFolder == @"C:\Saved" && saved.Profile == "Old", "the saved settings are not changed");

        inputs.Operation = GuiOperation.IncrementalBackup; inputs.ZipChecked = true; inputs.IncrementalName = "Second"; inputs.ChosenBackup = @"D:\Backups\2026-10-16";
        Config incremental = WindowOptions.Capture(saved, inputs, true);
        Assert(!incremental.Zip && incremental.IncrementalBackup == @"D:\Backups\2026-10-16" && incremental.IncrementalBackupName == "Second" && incremental.FullBackupName == saved.FullBackupName,
            "zip applies only to full backups; an incremental takes its set and name");
        inputs.ChosenBackup = null;
        Throws(() => WindowOptions.Capture(saved, inputs, true), "an incremental backup needs a chosen full backup");
        Assert(WindowOptions.Capture(saved, inputs, false).IncrementalBackup == saved.IncrementalBackup, "reading the options without an operation does not demand the choice");

        inputs.Operation = GuiOperation.Restore; inputs.RestoreDestination = @"F:\Restored";
        var request = new RestoreRequest { Backup = @"D:\Backups\2026-10-16", Point = "p" };
        inputs.RestoreSelection = () => request;
        Config restore = WindowOptions.Capture(saved, inputs, true);
        Assert(restore.SourceFolder == @"F:\Restored" && restore.RestoreSelection == request, "a restore uses its destination as the folder and carries the chosen restore");
        inputs.RestoreSelection = () => { throw new IOException("Choose a backup and restore point first."); };
        Throws(() => WindowOptions.Capture(saved, inputs, true), "a restore without a chosen point is refused");
        Assert(WindowOptions.Capture(saved, inputs, false).SourceFolder == @"E:\", "without an operation the typed source stays");

        inputs.Operation = GuiOperation.RestoreZip; inputs.ArchiveReady = false;
        Throws(() => WindowOptions.Capture(saved, inputs, true), "a ZIP restore needs a readable archive for the detected device");
        inputs.ArchiveReady = true; inputs.ArchivePath = @"D:\a.zip";
        Assert(WindowOptions.Capture(saved, inputs, true).RestoreArchive == @"D:\a.zip" && WindowOptions.Restoring(GuiOperation.RestoreZip), "a ZIP restore carries the archive");

        inputs.Operation = GuiOperation.Check;
        Assert(WindowOptions.Capture(saved, inputs, true).SourceFolder == "", "a check needs no source folder");
        inputs.BackupFolder = "  ";
        Assert(WindowOptions.Capture(saved, inputs, true).BackupFolder == AppSettings.DefaultBackupFolder(), "an empty backup location means the default");
        inputs.Profile = "bad/name";
        Throws(() => WindowOptions.Capture(saved, inputs, false), "an invalid profile name is refused");
        Assert(!WindowOptions.ZipApplies(GuiOperation.Restore) && WindowOptions.ZipApplies(GuiOperation.CameraSettings), "zip applies to full backups and camera settings");
    }
}
