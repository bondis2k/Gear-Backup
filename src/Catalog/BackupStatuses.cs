using System.IO;
using static AtomicFile;

// A backup folder's status record (.gearbackup\status.txt).
static class BackupStatuses
{
    // Status.Incomplete while a backup runs (or after it was interrupted), then Complete or Failed.
    // Missing status metadata is reported as unknown.
    internal static Status BackupStatus(string folder)
    {
        string path = Path.Combine(AppIdentity.MetadataRoot(folder), "status.txt");
        if (!File.Exists(path)) return Status.Unknown;
        return Status.FromText(Records.ReadText(path, Records.SmallLimit, "Backup status record").Trim());
    }

    internal static void SetBackupStatus(string folder, Status status)
    {
        AtomicText(Path.Combine(AppIdentity.MetadataRoot(folder), "status.txt"), status + "\n");
    }
}
