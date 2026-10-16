// The dated backup folders inside a history.
using System;
using System.Collections.Generic;
using System.IO;
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
        internal static void PruneBackups(string backupRoot, int keep, string current)
        {
            List<string> backups = ListBackups(backupRoot);
            backups.RemoveAll(b => string.Equals(b, current, StringComparison.OrdinalIgnoreCase));
            int extra = backups.Count + 1 - keep;
            if (extra <= 0) return;

            List<string> old = backups.GetRange(0, extra);
            OperationOutput.WriteLine("  KeepBackups is {0}, so these {1} old backup(s) can go:", keep, old.Count);
            foreach (string dir in old) OperationOutput.WriteLine("    " + Path.GetFileName(dir));
            if (!AskYesNo("Move them to the Recycle Bin?")) return;

            foreach (string dir in old)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    OperationOutput.WriteLine("  Moved to Recycle Bin: " + Path.GetFileName(dir));
                }
                catch (Exception ex)
                {
                    WriteColored("  Could not remove " + Path.GetFileName(dir) + ": " + ex.Message, ConsoleColor.Yellow);
                }
            }
            OperationOutput.WriteLine();
        }

        internal static string LatestBackup(string history)
        {
            List<string> backups = ListBackups(history);
            return backups.Count == 0 ? null : backups[backups.Count - 1];
        }
    }

}
