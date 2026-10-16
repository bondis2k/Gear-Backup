// Reading a content-store backup: is it one, and what is its current restore point.
using System;
using System.IO;
using static GearBackup.PathSafety;
using static GearBackup.Snapshots;

namespace GearBackup
{
    static class ContentStore
    {
        // Every backup is a content store, marked by storage-mode.txt (written first, so even a first backup that failed has it).
        // Backups made by older versions in a plain-folder layout have no marker and are no longer supported.
        internal static bool IsContentStore(string backup)
        {
            if (backup == null) return false;
            string path = Path.Combine(AppIdentity.MetadataRoot(backup), "storage-mode.txt");
            if (!File.Exists(path))
            {
                if (File.Exists(Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt")))
                    throw new IOException("Content-store backup is missing its storage-mode metadata.");
                return false;
            }
            string mode = Records.ReadText(path, Records.SmallLimit, "Backup storage mode").Trim();
            if (mode != "single") throw new IOException("Unknown backup storage mode.");
            return true;
        }

        internal static void RequireContentStore(string backup)
        {
            if (!IsContentStore(backup))
                throw new UnsupportedBackupException("This backup was made in a layout older versions of Gear Backup used, which is no longer supported: " + backup +
                    ". Make a new full backup; the old folder can stay for reference.");
        }
        internal static Snapshot Current(string backup, ObjectChecks objects = null)
        {
            string pointer = Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt");
            string name = Records.ReadText(pointer, Records.SmallLimit, "Current restore point").Trim();
            return ReadSnapshot(backup, SafePath(SnapshotRoot(backup), name + ".manifest"), objects);
        }
    }

}
