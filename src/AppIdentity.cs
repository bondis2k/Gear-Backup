using System.IO;

partial class GearBackup
{
    // Product identity is separate from the profiles of the devices we back up.
    internal static class AppIdentity
    {
        public const string Name = "Gear Backup";
        // Change this one value for a release; both executables and Setup use it.
        public const string Version = "1.0.0";
        public const string DirectoryName = "GearBackup";
        public const string ConfigFileName = DirectoryName + ".ini";
        public const string BackupsFolderName = "Gear Backups";
        public const string RecoveryFolderName = "Gear Recovery";
        public const string MetadataFolderName = ".gearbackup";
        public const string SnapshotHeader = "GEARSNAPSHOT\t1";
        public const string NotificationId = "GearBackup.Application";

        // Only compatibility code knows the former product's on-disk names.
        internal static class Legacy
        {
            public const string Name = "M8 Backup";
            public const string DirectoryName = "M8Backup";
            public const string ConfigFileName = DirectoryName + ".ini";
            public const string BackupsFolderName = "M8 Backups";
            public const string RecoveryFolderName = "M8 Recovery";
            public const string MetadataFolderName = ".m8backup";
            public const string SnapshotHeader = "M8SNAPSHOT\t1";
        }

        // Reuse an existing installation's folders without moving its files or breaking paths.
        public static string ExistingFolder(string parent, string name, string legacyName)
        {
            string current = Path.Combine(parent, name), legacy = Path.Combine(parent, legacyName);
            return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
        }

        public static string MetadataRoot(string backup)
        {
            return ExistingFolder(backup, MetadataFolderName, Legacy.MetadataFolderName);
        }

        public static bool IsMetadataPath(string path)
        {
            string normalized = path.Replace('/', '\\');
            foreach (string name in new[] { MetadataFolderName, Legacy.MetadataFolderName })
                if (normalized.EndsWith("\\" + name, System.StringComparison.OrdinalIgnoreCase) ||
                    normalized.IndexOf("\\" + name + "\\", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
