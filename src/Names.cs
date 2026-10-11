using static GearBackup;
using static ConsoleUi;
// Named values for things that used to be typed as text in several places, so a typo is now a
// compile error instead of a silent bug.
// Progress stages the engines report to the front ends.
internal static class Stage
{
    public const string Scanning = "Scanning", PlanningBackup = "Planning backup", BackupCopy = "Backup copy",
        Copying = "Copying", Hashing = "Hashing", Verifying = "Verifying", Comparing = "Comparing",
        SavingHistory = "Saving history", SavingChecksums = "Saving checksums", Zipping = "Zipping",
        ScanningBackup = "Scanning backup", CheckingBackup = "Checking backup", CheckingHistory = "Checking history",
        PlanningRestore = "Planning restore", Restoring = "Restoring", SavingRecovery = "Saving recovery",
        Downloading = "Downloading";

    // Per-file detail. During a full backup only overall progress is shown, so front ends skip these.
    public static bool IsFileDetail(string stage)
    {
        return stage == Copying || stage == Hashing || stage == Verifying || stage == Comparing || stage == ScanningBackup;
    }
}

// A backup folder's status, stored in .gearbackup\status.txt. The text values are what's on disk.
internal static class Status
{
    public const string Incomplete = "incomplete", Complete = "complete", Failed = "failed";
    // Reported for backups made before status files existed.
    public const string Legacy = "legacy (status unknown)";

    // An interrupted or failed backup, which can be resumed.
    public static bool Unfinished(string status) { return status == Incomplete || status == Failed; }
}

// What a restore brings back. Each value is also the key typed in the console menu.
internal static class RestoreMode
{
    public const char All = 'a', Bundle = 'b', ProjectFolder = 'p', SingleFile = 's', SongWithSamples = 'd';
}

// The kinds of item Storage offers for cleanup; the text is shown in its table.
internal static class StorageKind
{
    public const string OlderBackup = "Older backup", OldVersion = "Old version", Zip = "ZIP", Mirror = "MTP mirror";
}

// Mode setting: what to do when a backup already exists.
internal enum BackupMode { Ask, Full, Incremental }

// Eject setting: what to do with the M8 when finished.
internal enum EjectMode { No, Ask, Yes }
