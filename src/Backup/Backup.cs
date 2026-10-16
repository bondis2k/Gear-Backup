// Full and incremental backups, the backup folders, and scanning the M8.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using static ConsoleUi;
using static Backups;

static class Backups
{
    // The date/time suffix added to old versions of updated files, e.g. "Song_2026-10-01_143000" or "..._2"

    // The full result, including whether the backup was cancelled. Title is null if nothing was done.
    internal static OperationResult RunBackup(string source, string backupRoot, string previous, bool incremental, Config config)
    {
        return new BackupEngine().Run(source, backupRoot, previous, incremental, config, OperationContext.Current);
    }

    // Returns the exit code; title/summary are for the final notification (null if nothing was done).
    internal static int Backup(string source, string backupRoot, string previous, bool incremental, Config config,
                      out string title, out string summary)
    {
        OperationResult result = RunBackup(source, backupRoot, previous, incremental, config);
        title = result.Title;
        summary = result.Summary;
        return result.ExitCode;
    }

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

    // Linux disk-check (fsck.fat) recovered fragments: FSCK0000.REC, FSCK0001.REC, ...
    static readonly Regex FsckFile = new Regex(@"^FSCK\d{4}\.REC$", RegexOptions.IgnoreCase);

    // Leftovers other computers write to memory cards, which the M8 doesn't use:
    // - names starting with "." — macOS (.DS_Store, ._*, .Spotlight-V100, .fseventsd, .Trashes) and
    //   Linux (.Trash-1000, .directory, .hidden, .xdg-volume-info, .goutputstream-*, .fuse_hidden*)
    // - Linux: the lost+found folder, FSCK*.REC files, and editor backup files ending in "~"
    // - Windows: Thumbs.db, ehthumbs.db (thumbnail caches) and desktop.ini (folder view settings)
    internal static bool IsLeftover(string name, bool isFolder)
    {
        if (name.StartsWith(".")) return true;
        if (isFolder) return name == "lost+found";
        return name.EndsWith("~") || FsckFile.IsMatch(name) ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ehthumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    // Walks the tree, skipping Windows system folders and leftovers from other computers (see IsLeftover).
    internal static void CollectFiles(string dir, ScanResult scan)
    {
        OperationContext.Current.Progress(Stage.Scanning, dir, 0, 0, scan.Files.Count);
        try
        {
            foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles())
            {
                OperationContext.Current.CheckCancellation();
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked file is not supported: " + file.FullName);
                if (IsLeftover(file.Name, false))
                {
                    scan.SkippedFiles++;
                    scan.SkippedFileBytes += file.Length;
                    if (!file.Name.StartsWith(".")) scan.SkippedNamed.Add(file.FullName);
                }
                else
                {
                    scan.Files.Add(file);
                }
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                OperationContext.Current.CheckCancellation();
                string name = Path.GetFileName(sub);
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked folder is not supported: " + sub);
                if (name == "System Volume Information" || name == "$RECYCLE.BIN") continue;
                if (IsLeftover(name, true))
                {
                    scan.SkippedFolders++;
                    if (!name.StartsWith(".")) scan.SkippedNamed.Add(sub + "\\");
                    try
                    {
                        foreach (FileInfo f in new DirectoryInfo(sub).GetFiles("*", SearchOption.AllDirectories))
                        {
                            OperationContext.Current.CheckCancellation();
                            scan.SkippedFolderFiles++;
                            scan.SkippedFolderBytes += f.Length;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { }  // only used for the statistics
                    continue;
                }
                scan.Folders.Add(sub);
                CollectFiles(sub, scan);
            }
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("Cannot scan " + dir + ": " + ex.Message, ex); }
    }
}

internal class ScanResult
{
    public List<FileInfo> Files = new List<FileInfo>();
    public List<string> Folders = new List<string>();
    public int SkippedFolders, SkippedFolderFiles, SkippedFiles;
    public long SkippedFolderBytes, SkippedFileBytes;
    // Skipped items without a leading "." (Linux and Windows leftovers), listed by name in the statistics
    public List<string> SkippedNamed = new List<string>();
}
