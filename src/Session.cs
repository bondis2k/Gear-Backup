// Workflow steps shared by the CLI (Program.cs) and the GUI (GuiOperationRunner.cs), so both front
// ends detect sources, apply preferences, pick and lock histories, resume and eject by the same rules.
using System;
using System.Collections.Generic;
using System.IO;

partial class GearBackup
{
    // Where an operation's files come from (or, for a restore, go to).
    internal sealed class SourceChoice
    {
        public string Path;             // the drive or folder; for an MTP device, its mirror
        public string PnpId;            // set only for a detected drive, which can then be ejected
        public DeviceProfile Detected;  // the detected device; null for a folder chosen by hand
        public MtpSource Mtp;           // set for an MTP device, whose mirror is synced before a backup
        public string CameraMode;       // the X100VI's connection mode, read once at detection
        public bool CameraSettingsMode;
    }

    // A source with everything both front ends work out the same way: its device, its preferences
    // (global, then device, then profile) and its history.
    internal sealed class PreparedSource
    {
        public SourceChoice Source;
        public string Path, Histories, History;
        public DeviceProfile Device;
        public Config Global, Config;
    }

    // The first connected supported device: a USB drive, otherwise an MTP device (whose mirror folder
    // is created). Null when none is connected.
    static SourceChoice DetectSource()
    {
        DeviceProfile device;
        string pnp;
        string drive = FindDevice(out device, out pnp);
        if (drive != null) return new SourceChoice { Path = drive, PnpId = pnp, Detected = device };
        MtpSource mtp = FindMtpSource();
        if (mtp == null) return null;
        Directory.CreateDirectory(mtp.Mirror);
        var found = new SourceChoice { Path = mtp.Mirror, Detected = mtp.Device, Mtp = mtp };
        if (mtp.Device == Devices.X100VI)
        {
            found.CameraSettingsMode = FujiSettingsMode(mtp.Info);
            found.CameraMode = FujiModeName(found.CameraSettingsMode);
        }
        return found;
    }

    // Checks the source and works out its device, preferences and history. Sets the current device.
    static PreparedSource PrepareSource(Config global, SourceChoice choice)
    {
        if (!Directory.Exists(choice.Path)) throw new IOException("Source folder does not exist: " + choice.Path);
        string path = Rooted(choice.Path);
        RejectRootLinks(path);
        DeviceProfile device = DeviceFor(global, choice.Detected, path);
        OperationContext.Current.Device = device;
        Config config = ResolveSourceSettings(global, path);
        return new PreparedSource { Source = choice, Path = path, Device = device, Global = global, Config = config,
            Histories = HistoriesRoot(config), History = HistoryFor(config, path) };
    }

    // Runs a backup of the prepared source, or a restore to it from a history the user picks. An MTP
    // device's mirror is synced first, under the lock of the folder holding the mirrors (MirrorRoot),
    // which storage cleanup also takes before recycling a mirror.
    static OperationResult RunPrepared(PreparedSource prepared, bool restore, bool incremental)
    {
        MtpSource mtp = prepared.Source.Mtp;
        if (restore && mtp != null) throw new IOException(MtpRestoreUnsupported(mtp.Device));
        if (!restore && !EnsureBackupFolder(prepared.Config)) return BackupFolderCancelled();
        using (mtp == null ? null : LockHistory(Path.GetDirectoryName(Rooted(mtp.Mirror).TrimEnd('\\'))))
        {
            string source = mtp == null ? prepared.Path : SyncMtpSource(mtp);
            if (!restore) return BackupWithLock(source, prepared.History, incremental, prepared.Config);
            string history = ChooseHistory(prepared.Global, prepared.History);
            if (history == null) return new OperationResult { Cancelled = true, Summary = "Restore cancelled." };
            List<string> backups = ListBackups(history);
            if (backups.Count == 0) throw new IOException("No backups found at this location.");
            OperationResult result;
            // Resolve device and profile preferences after the backup itself is selected.
            using (LockUnlessPreview(history)) result = RunRestore(source, backups, prepared.Config, prepared.Global);
            result.Cancelled |= OperationContext.Current.Cancellation.IsCancellationRequested;
            return result;
        }
    }

    static OperationResult BackupFolderCancelled()
    {
        return new OperationResult { Cancelled = true, Summary = "Backup cancelled. The destination folder was not created." };
    }

    // Ask before the history lock or MTP sync creates anything for a backup. A preview stays read-only.
    static bool EnsureBackupFolder(Config config)
    {
        string folder = Path.GetFullPath(config.BackupFolder);
        RejectRootLinks(folder);
        if (Directory.Exists(folder)) return true;
        if (File.Exists(folder)) throw new IOException("The backup location is a file, not a folder: " + folder);
        if (PreviewOnly) return true;
        if (!AskYesNo("The backup folder does not exist:\n" + folder + "\n\nCreate it and continue with the backup?")) return false;
        OperationContext.Current.CheckCancellation();
        RejectRootLinks(folder);
        Directory.CreateDirectory(folder);
        return true;
    }

    static string HistoriesRoot(Config config)
    {
        return AppIdentity.ExistingFolder(config.BackupFolder, AppIdentity.BackupsFolderName, AppIdentity.Legacy.BackupsFolderName);
    }

    // Checks the source against the backup location and returns this source's history folder.
    static string HistoryFor(Config config, string source)
    {
        string histories = HistoriesRoot(config);
        if (Within(histories, source) || Within(source, histories))
            throw new IOException("The backup folder and source must not contain one another.");
        return ProfileFolder(Path.Combine(histories, "profiles"), source, config.Profile);
    }

    // The history to check: the named profile, or one the user picks. Null when cancelled.
    static string HistoryToCheck(Config config)
    {
        string histories = HistoriesRoot(config);
        if (config.Profile.Length == 0) return ChooseHistory(config, null);
        var found = AvailableHistories(config).FindAll(path =>
            Path.GetFileName(Path.GetDirectoryName(path)).Equals("profiles", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).Equals(config.Profile, StringComparison.OrdinalIgnoreCase));
        if (found.Count == 1) return found[0];
        if (found.Count > 1) { int pick = AskItem("Choose the location for this profile.", found); return pick == 0 ? null : found[pick - 1]; }
        return Path.Combine(histories, "profiles", config.Profile);
    }

    // Locks a history against a second writer; a preview changes nothing, so it takes no lock.
    static IDisposable LockUnlessPreview(string history)
    {
        return PreviewOnly ? null : LockHistory(history);
    }

    static string LatestBackup(string history)
    {
        List<string> backups = ListBackups(history);
        return backups.Count == 0 ? null : backups[backups.Count - 1];
    }

    // When the latest backup didn't finish, asks whether to resume it. Returns the incremental choice
    // to use (unchanged if the latest backup is fine), or null when the user cancels.
    static bool? ConfirmResume(string previous, bool incremental, Config config)
    {
        if (previous == null) return incremental;
        string status = BackupStatus(previous);
        if (!Status.Unfinished(status)) return incremental;
        OperationOutput.WriteLine("  Latest backup is " + status + ". Resume uses " +
            (config.Thorough ? "SHA-256 content comparison." : "size and modified time (Thorough=no)."));
        char answer = Ask("Resume the unfinished backup?", new Option('r', "Resume into it"),
            new Option('n', "New full backup"), new Option('c', "Cancel"));
        if (answer == 'c') return null;
        return answer == 'r';
    }

    // Runs a backup while holding the history lock. The latest backup is read under the lock, so a
    // backup finished by another window in the meantime is seen. `incremental` is the user's choice;
    // it's ignored when there's no backup yet.
    static OperationResult BackupWithLock(string source, string history, bool incremental, Config config)
    {
        using (LockUnlessPreview(history))
        {
            string previous = LatestBackup(history);
            if (previous == null) OperationOutput.WriteLine("  First backup for this profile: creating a full backup.");
            bool? choice = ConfirmResume(previous, incremental && previous != null, config);
            if (choice == null) return new OperationResult { Cancelled = true, Summary = "Backup cancelled." };
            return RunBackup(source, history, previous, choice.Value, config);
        }
    }

    // The M8 can be unplugged or get another drive letter while a window stays open. Only eject a
    // device that still owns this source; returns null when it does, otherwise the reason.
    static string ConfirmSameDevice(string source, string pnpId)
    {
        try
        {
            string currentPnp;
            DeviceProfile currentDevice;
            string currentSource = FindDevice(out currentDevice, out currentPnp);
            if (currentSource == null || Rooted(currentSource) != Rooted(source) || currentPnp != pnpId)
                return "The detected device changed, so it was left connected.";
            return null;
        }
        catch (Exception ex) { return "Could not confirm the device for ejection: " + ex.Message; }
    }
}
