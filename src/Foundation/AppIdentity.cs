using System.IO;

namespace GearBackup
{
    // Product identity is separate from the profiles of the devices we back up.
    internal static class AppIdentity
    {
        public const string Name = "Gear Backup";
        // Display version supports suffixes; Windows binary metadata requires numbers.
        public const string Version = "0.9.5";
        public const string BinaryVersion = "0.9.5.0";
        public const string DirectoryName = "GearBackup";
        public const string ConfigFileName = DirectoryName + ".ini";
        public const string ProfileConfigFileName = DirectoryName + ".Profiles.ini";
        public const string InstalledMarker = "GearBackup.installed";
        public const string BackupsFolderName = "Gear Backups";
        public const string RecoveryFolderName = "Gear Recovery";
        public const string MetadataFolderName = ".gearbackup";
        public const string SnapshotHeader = "GEARSNAPSHOT\t1";
        public const string NotificationId = "GearBackup.Application";

        public static string MetadataRoot(string backup)
        {
            return Path.Combine(backup, MetadataFolderName);
        }

        public static bool IsMetadataPath(string path)
        {
            string normalized = path.Replace('/', '\\');
            return normalized.EndsWith("\\" + MetadataFolderName, System.StringComparison.OrdinalIgnoreCase) ||
                normalized.IndexOf("\\" + MetadataFolderName + "\\", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

}
