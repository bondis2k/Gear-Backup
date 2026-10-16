// The dated backup folders inside a history.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static GearBackup.ConsoleUi;
using static GearBackup.BackupFolders;

namespace GearBackup
{
    static class BackupFolders
    {
        // Backup folders (named yyyy-MM-dd or yyyy-MM-dd_HHmmss) in backupRoot, oldest first.
        internal static List<string> ListBackups(string backupRoot)
        {
            var backups = new List<string>();
            if (!Directory.Exists(backupRoot)) return backups;
            foreach (string dir in Directory.GetDirectories(backupRoot))
            {
                // A link (junction) is never a backup: it could point anywhere, and KeepBackups would
                // otherwise offer it for deletion. Every other operation rejects links too.
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                string name = Path.GetFileName(dir);
                DateTime date;
                if (name.Length >= 10 && DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date))
                    backups.Add(dir);
            }
            backups.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            return backups;
        }

        // Moves the oldest backups to the Recycle Bin (after asking) so only `keep` remain.
        internal static void PruneBackups(string backupRoot, int keep, string current, Action<string> recycle = null)
        {
            PathSafety.RejectRootLinks(backupRoot);
            List<string> backups = ListBackups(backupRoot);
            backups.RemoveAll(b => string.Equals(b, current, StringComparison.OrdinalIgnoreCase));
            int extra = backups.Count + 1 - keep;
            if (extra <= 0) return;

            List<string> old = backups.GetRange(0, extra);
            var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in old)
            {
                stamps[dir] = RetentionStamp(dir);
            }
            OperationOutput.WriteLine("  KeepBackups is {0}, so these {1} old backup(s) can go:", keep, old.Count);
            foreach (string dir in old) OperationOutput.WriteLine("    " + Path.GetFileName(dir));
            if (!AskYesNo("Move them to the Recycle Bin?")) return;

            // Check the whole approved set before removing anything, then each folder again just before recycling.
            foreach (string dir in old) ValidateRetentionCandidate(backupRoot, dir, current, stamps[dir]);

            foreach (string dir in old)
            {
                try
                {
                    OperationContext.Current.CheckCancellation();
                    ValidateRetentionCandidate(backupRoot, dir, current, stamps[dir]);
                    if (recycle != null) recycle(dir);
                    else Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    OperationOutput.WriteLine("  Moved to Recycle Bin: " + Path.GetFileName(dir));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    WriteColored("  Could not remove " + Path.GetFileName(dir) + ": " + ex.Message, ConsoleColor.Yellow);
                }
            }
            OperationOutput.WriteLine();
        }

        static void ValidateRetentionCandidate(string root, string path, string current, string stamp)
        {
            PathSafety.RejectRootLinks(root); PathSafety.RejectRootLinks(path);
            string latest = LatestBackup(root);
            if (!PathSafety.Within(path, root) || PathSafety.Rooted(path) == PathSafety.Rooted(root) ||
                PathSafety.Rooted(path) == PathSafety.Rooted(current) ||
                string.Equals(PathSafety.Rooted(path), latest == null ? null : PathSafety.Rooted(latest), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Retention candidate is no longer safe to remove: " + path);
            if (RetentionStamp(path) != stamp)
                throw new IOException("Backup changed since the retention preview: " + path);
        }

        static string RetentionStamp(string path)
        {
            PathSafety.RejectRootLinks(path);
            // A replacement directory can have the same names, sizes and times. Remember the actual file identity too.
            string identity = DirectoryIdentity(path);
            long bytes;
            string stamp = StorageCatalog.StorageStamp(path, true, out bytes);
            if (identity != DirectoryIdentity(path)) throw new IOException("Backup directory changed while scanning: " + path);
            return identity + ":" + stamp;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DirectoryInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out DirectoryInformation information);

        static string DirectoryIdentity(string path)
        {
            // BACKUP_SEMANTICS opens directories; OPEN_REPARSE_POINT inspects a substituted link itself.
            using (var handle = CreateFileW(path.TrimEnd('\\'), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                DirectoryInformation information;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out information))
                    throw new IOException("Cannot identify retention directory: " + path, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new UnsafePathException("Linked retention directory: " + path);
                return information.Volume + ":" + information.IndexHigh + ":" + information.IndexLow;
            }
        }

        internal static string LatestBackup(string history)
        {
            List<string> backups = ListBackups(history);
            return backups.Count == 0 ? null : backups[backups.Count - 1];
        }
    }

}
