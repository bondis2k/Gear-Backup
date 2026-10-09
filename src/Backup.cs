// Full and incremental backups, the backup folders, and scanning the M8.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

partial class M8Backup
{
    // The date/time suffix added to old versions of updated files, e.g. "Song_2026-10-01_143000" or "..._2"
    static readonly Regex VersionSuffix = new Regex(@"_\d{4}-\d{2}-\d{2}_\d{6}(_\d+)?$");

    // Returns the exit code; title/summary are for the final notification (null if nothing was done).
    static int Backup(string source, string backupRoot, string previous, bool incremental, Config config,
                      out string title, out string summary)
    {
        title = summary = null;
        DateTime started = DateTime.Now;
        string dest;
        if (incremental)
        {
            dest = previous;
        }
        else
        {
            dest = UniqueFolder(backupRoot, started.ToString("yyyy-MM-dd"));
            if (Directory.Exists(dest))
                dest = Path.Combine(backupRoot, started.ToString("yyyy-MM-dd_HHmmss"));
        }

        Console.WriteLine((incremental ? "  Updating " : "  Backing up to ") + dest);
        Console.WriteLine();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var scan = new ScanResult();
        CollectFiles(source, scan);

        // Compare every file with the last backup: tells what changed, and what an incremental run must copy
        bool resuming = incremental && (BackupStatus(dest) == "incomplete" || BackupStatus(dest) == "failed");
        var states = new FileState[scan.Files.Count];
        var plan = new List<string>();
        Dictionary<string, string> oldSums = previous == null ? null : LoadChecksums(previous);
        var pendingObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0, neededBytes = 0;
        long historyBytes = 0;
        for (int i = 0; i < scan.Files.Count; i++)
        {
            FileInfo file = scan.Files[i];
            states[i] = previous == null
                ? FileState.New
                : CompareContent(file, SafePath(previous, file.FullName.Substring(source.Length)), config.Thorough || resuming);
            totalBytes += file.Length;
            if (!incremental || states[i] != FileState.Same) neededBytes += file.Length;
            string relative = file.FullName.Substring(source.Length);
            plan.Add((!incremental ? "Copy: " : states[i] == FileState.New ? "New: " : states[i] == FileState.Changed ? "Changed: " : "Skip unchanged: ") + relative);
            string hash;
            if (!resuming && incremental && states[i] == FileState.Same && oldSums != null && oldSums.TryGetValue(relative, out hash)) { }
            else hash = HashFile(file.FullName);
            if (!File.Exists(ObjectPath(dest, hash)) && pendingObjects.Add(hash)) historyBytes += file.Length;
        }
        Changes changes = FindChanges(scan, source, states, previous);

        List<string> nameWarnings = CheckNames(scan, source, config.MaxNameLength);
        if (nameWarnings.Count > 0)
            WriteColored("  " + nameWarnings.Count + " name(s) the M8 may not handle well (listed in the statistics).", ConsoleColor.Yellow);

        Console.WriteLine("  History store needs up to {0} additional space (identical content is shared).", FormatSize(historyBytes));
        string spaceError = CheckFreeSpace(backupRoot, neededBytes + historyBytes);
        if (spaceError != null)
        {
            WriteColored("  " + spaceError, ConsoleColor.Red);
            ShowToast("M8 backup not started", spaceError, false);
            return 1;
        }

        if (!ShowPlan(resuming ? "Resume backup" : "Backup preview", plan, neededBytes, config)) return 0;
        // Mark before the first mutation. A killed process leaves an incomplete status.
        Directory.CreateDirectory(dest);
        SetBackupStatus(dest, "incomplete");
        foreach (string folder in scan.Folders)
            Directory.CreateDirectory(Path.Combine(dest, folder.Substring(source.Length)));
        int folders = scan.Folders.Count;

        int copied = 0, failed = 0, updated = 0, unchanged = 0;
        long copiedBytes = 0, processedBytes = 0;
        FileInfo largest = null;
        var failures = new List<string>();
        var keptVersions = new List<string>();
        // Per top-level folder (Songs, Samples, ...): [files, bytes]
        var perFolder = new SortedDictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        // Checksums for the backup's checksum list; an incremental run starts from the existing list
        var sums = (incremental ? LoadChecksums(dest) : null) ??
                   new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < scan.Files.Count; i++)
        {
            FileInfo file = scan.Files[i];
            string relative = file.FullName.Substring(source.Length);
            string target = SafePath(dest, relative);
            processedBytes += file.Length;
            if (incremental && states[i] == FileState.Same)
            {
                if (resuming) sums[relative] = HashFile(target);
                unchanged++;
                PrintProgress(processedBytes, totalBytes, copied + unchanged, scan.Files.Count, copiedBytes);
                continue;
            }

            string oldVersion = null;
            try
            {
                if (incremental && states[i] == FileState.Changed)
                {
                    // Changed on the M8: keep the old copy, renamed with its own modified date and time
                    string expectedOld;
                    bool hasOldHash = sums.TryGetValue(relative, out expectedOld);
                    bool suspect = (hasOldHash && HashFile(target) != expectedOld) || (resuming && !hasOldHash);
                    if (suspect)
                    {
                        oldVersion = SafePath(Path.Combine(dest, ".m8backup", "quarantine", Guid.NewGuid().ToString("N")), relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(oldVersion));
                        WriteColored("  Preserving unverified/partial existing copy in quarantine: " + relative, ConsoleColor.Yellow);
                    }
                    else oldVersion = VersionedName(target, File.GetLastWriteTime(target));
                    File.Move(target, oldVersion);
                }
                string hash = CopyFile(file, target, config.Verify);
                copied++;
                if (oldVersion != null)
                {
                    updated++;
                    string oldRelative = oldVersion.Substring(dest.Length).TrimStart('\\');
                    keptVersions.Add(oldRelative);
                    // The old version keeps the checksum it had under its original name
                    string oldHash;
                    if (!IsBackupMetaFile(oldVersion) && sums.TryGetValue(relative, out oldHash)) sums[oldRelative] = oldHash;
                }
                sums[relative] = hash;
                copiedBytes += file.Length;
                if (largest == null || file.Length > largest.Length) largest = file;

                int slash = relative.IndexOf('\\');
                string top = slash < 0 ? "(root)" : relative.Substring(0, slash);
                long[] entry;
                if (!perFolder.TryGetValue(top, out entry)) perFolder[top] = entry = new long[2];
                entry[0]++;
                entry[1] += file.Length;
            }
            catch (Exception ex)
            {
                // Put the old copy back if the new one couldn't be copied
                if (oldVersion != null && File.Exists(oldVersion) && !File.Exists(target))
                    try { File.Move(oldVersion, target); } catch (Exception) { }
                failed++;
                string message = relative + " (" + ex.Message + ")";
                failures.Add(message);
                Console.WriteLine();
                WriteColored("  Failed: " + message, ConsoleColor.Red);
            }
            PrintProgress(processedBytes, totalBytes, copied + unchanged, scan.Files.Count, copiedBytes);
        }
        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine();

        int prunedVersions = 0;
        try
        {
            // Populate missing hashes for verified resume/legacy files before creating history.
            foreach (FileInfo f in scan.Files)
            {
                string rel = f.FullName.Substring(source.Length);
                if (!sums.ContainsKey(rel)) sums[rel] = HashFile(SafePath(dest, rel));
            }
            if (failed == 0) SaveSnapshot(dest, source, scan, sums, started);
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add("Point-in-time history: " + ex.Message);
            WriteColored("  Could not save point-in-time history: " + ex.Message, ConsoleColor.Red);
        }
        if (failed == 0 && incremental && config.KeepOldVersions > 0)
        {
            var onM8 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in scan.Files) onM8.Add(f.FullName.Substring(source.Length));
            prunedVersions = PruneOldVersions(dest, config.KeepOldVersions, onM8);
        }

        // Checksum list for the whole backup folder (files without a known checksum are read and hashed)
        string checksumStatus;
        try
        {
            Console.WriteLine("  Saving checksum list...");
            int listed = WriteChecksums(dest, sums);
            checksumStatus = string.Format("{0:N0} files listed in {1}", listed, ChecksumFileName);
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add("Checksum list: " + ex.Message);
            checksumStatus = "could not be saved: " + ex.Message;
            WriteColored("  Checksum list " + checksumStatus, ConsoleColor.Yellow);
        }
        Console.WriteLine();

        // Statistics, written to both the console and a text file in the backup folder
        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var report = new StringWriter();
        report.WriteLine("  ----------------------------------------");
        report.WriteLine("  Date:          {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now);
        report.WriteLine("  Type:          {0}", incremental ? "Incremental" : "Full");
        report.WriteLine("  Source:        {0}", source);
        report.WriteLine("  Destination:   {0}", dest);
        report.WriteLine("  Files copied:  {0:N0}", copied);
        if (incremental)
        {
            report.WriteLine("    New:         {0:N0}", copied - updated);
            report.WriteLine("    Updated:     {0:N0}", updated);
            report.WriteLine("  Unchanged:     {0:N0}", unchanged);
        }
        report.WriteLine("  Folders:       {0:N0}", folders);
        report.WriteLine("  Data copied:   {0}", FormatSize(copiedBytes));
        report.WriteLine("  Time:          {0}", FormatTime(stopwatch.Elapsed));
        report.WriteLine("  Average speed: {0}/s", FormatSize((long)(copiedBytes / seconds)));
        report.WriteLine("  Verified:      {0}", config.Verify
            ? "yes (each copy read back and its SHA-256 checksum compared)"
            : "no (Verify=no in M8Backup.ini)");
        report.WriteLine("  Checksums:     {0}", checksumStatus);
        if (largest != null)
            report.WriteLine("  Largest file:  {0} ({1})", largest.FullName.Substring(source.Length), FormatSize(largest.Length));
        report.WriteLine();

        WriteChanges(report, changes, previous);

        report.WriteLine("  {0,-16}{1,10}{2,14}", "Folder", "Files", "Size");
        foreach (var pair in perFolder)
            report.WriteLine("  {0,-16}{1,10:N0}{2,14}", pair.Key, pair.Value[0], FormatSize(pair.Value[1]));
        report.WriteLine();

        report.WriteLine("  Skipped (leftovers from macOS, Linux and Windows, e.g. .DS_Store, .Trash-1000, Thumbs.db):");
        report.WriteLine("    {0:N0} folder(s) containing {1:N0} files, {2}", scan.SkippedFolders, scan.SkippedFolderFiles, FormatSize(scan.SkippedFolderBytes));
        report.WriteLine("    {0:N0} file(s), {1}", scan.SkippedFiles, FormatSize(scan.SkippedFileBytes));
        foreach (string path in scan.SkippedNamed)
            report.WriteLine("    skipped: " + path.Substring(source.Length));

        var m8Drive = new DriveInfo(Path.GetPathRoot(source));
        report.WriteLine("  Free space on M8:       {0} of {1}", FormatSize(m8Drive.AvailableFreeSpace), FormatSize(m8Drive.TotalSize));
        string destRoot = Path.GetPathRoot(dest);
        report.WriteLine("  Free space left on {0}  {1}", destRoot, FreeSpaceText(destRoot));
        report.WriteLine();

        if (nameWarnings.Count > 0)
        {
            report.WriteLine("  Names the M8 may not handle well ({0}):", nameWarnings.Count);
            foreach (string w in nameWarnings) report.WriteLine("    " + w);
            report.WriteLine();
        }

        if (keptVersions.Count > 0)
        {
            report.WriteLine("  Old versions of updated files, kept as ({0}):", keptVersions.Count);
            foreach (string f in keptVersions) report.WriteLine("    " + f);
            report.WriteLine();
        }
        if (prunedVersions > 0)
        {
            report.WriteLine("  Older versions moved to the Recycle Bin (KeepOldVersions={0}): {1:N0}", config.KeepOldVersions, prunedVersions);
            report.WriteLine();
        }

        if (failures.Count > 0)
        {
            report.WriteLine("  Failed files ({0}):", failures.Count);
            foreach (string f in failures) report.WriteLine("    " + f);
            report.WriteLine();
        }
        string status = failed == 0
            ? "Backup complete."
            : "Backup finished with " + failed + " failed file(s).";

        WriteColored("  Statistics", ConsoleColor.Cyan);
        Console.Write(report.ToString());

        // An incremental run gets its own dated file so the earlier statistics are kept
        string statsFile = Path.Combine(dest, incremental
            ? "backup-statistics_" + started.ToString("yyyy-MM-dd_HHmmss_fffffff") + ".txt"
            : "backup-statistics.txt");
        try
        {
            File.WriteAllText(statsFile, "  M8 " + (incremental ? "incremental " : "") + "backup statistics\r\n" + report + "  " + status + "\r\n");
            Console.WriteLine("  Statistics saved to " + statsFile);
        }
        catch (Exception ex)
        {
            WriteColored("  Could not save statistics file: " + ex.Message, ConsoleColor.Yellow);
        }
        Console.WriteLine();

        SetBackupStatus(dest, failed == 0 ? "complete" : "failed");
        WriteColored("  " + status, failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
        Console.WriteLine();

        // Only tidy up old backups once this one is known to be good
        if (failed == 0 && config.KeepBackups > 0)
            PruneBackups(backupRoot, config.KeepBackups, dest);

        title = failed == 0 ? "M8 backup complete" : "M8 backup finished with errors";
        summary = string.Format("{0:N0} files, {1} copied in {2}.", copied, FormatSize(copiedBytes), FormatTime(stopwatch.Elapsed));
        if (failed > 0) summary += " " + failed + " file(s) failed.";
        summary += ShortSongSummary(changes);
        return failed == 0 ? 0 : 2;
    }

    // "Songs\Song.m8s" -> "Songs\Song_2026-10-01_143000.m8s" (adds _2, _3, ... if that name is taken)
    static string VersionedName(string path, DateTime time)
    {
        string baseName = Path.Combine(Path.GetDirectoryName(path),
            Path.GetFileNameWithoutExtension(path) + "_" + time.ToString("yyyy-MM-dd_HHmmss"));
        string ext = Path.GetExtension(path);
        string candidate = baseName + ext;
        for (int n = 2; File.Exists(candidate); n++) candidate = baseName + "_" + n + ext;
        return candidate;
    }

    static bool IsOldVersion(string path)
    {
        return VersionSuffix.IsMatch(Path.GetFileNameWithoutExtension(path));
    }

    // "Songs\Song_2026-10-01_143000.m8s" -> "Songs\Song.m8s"
    static string OriginalName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(Path.GetDirectoryName(path),
            name.Substring(0, VersionSuffix.Match(name).Index) + Path.GetExtension(path));
    }

    // Keeps at most `keep` old versions of each file in the backup; the oldest extra ones go to the
    // Recycle Bin after asking. Files that also exist on the M8 under that name are never touched
    // (e.g. a dated version that was restored to the M8). Returns how many were moved.
    static int PruneOldVersions(string backup, int keep, HashSet<string> onM8)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
        {
            if (IsBackupMetaFile(path) || !IsOldVersion(path)) continue;
            if (onM8.Contains(path.Substring(backup.Length).TrimStart('\\'))) continue;
            string original = OriginalName(path);
            List<string> list;
            if (!groups.TryGetValue(original, out list)) groups[original] = list = new List<string>();
            list.Add(path);
        }

        var extra = new List<string>();
        foreach (List<string> versions in groups.Values)
        {
            if (versions.Count <= keep) continue;
            // The date/time in the name sorts oldest first
            versions.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            extra.AddRange(versions.GetRange(0, versions.Count - keep));
        }
        if (extra.Count == 0) return 0;

        extra.Sort(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("  KeepOldVersions is {0}, so these {1} older version(s) can go:", keep, extra.Count);
        for (int i = 0; i < extra.Count && i < 15; i++)
            Console.WriteLine("    " + extra[i].Substring(backup.Length).TrimStart('\\'));
        if (extra.Count > 15) Console.WriteLine("    ... and {0} more", extra.Count - 15);
        if (!AskInConsole("  Move them to the Recycle Bin? (y/n) "))
        {
            Console.WriteLine();
            return 0;
        }

        int moved = 0;
        foreach (string path in extra)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                moved++;
            }
            catch (Exception ex)
            {
                WriteColored("  Could not remove " + Path.GetFileName(path) + ": " + ex.Message, ConsoleColor.Yellow);
            }
        }
        Console.WriteLine("  Moved {0} older version(s) to the Recycle Bin.", moved);
        Console.WriteLine();
        return moved;
    }

    static bool IsStatisticsFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("backup-statistics", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    // Backup folders (named yyyy-MM-dd or yyyy-MM-dd_HHmmss) in backupRoot, oldest first.
    static List<string> ListBackups(string backupRoot)
    {
        var backups = new List<string>();
        if (!Directory.Exists(backupRoot)) return backups;
        foreach (string dir in Directory.GetDirectories(backupRoot))
        {
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
    static void PruneBackups(string backupRoot, int keep, string current)
    {
        List<string> backups = ListBackups(backupRoot);
        backups.RemoveAll(b => string.Equals(b, current, StringComparison.OrdinalIgnoreCase));
        int extra = backups.Count + 1 - keep;
        if (extra <= 0) return;

        List<string> old = backups.GetRange(0, extra);
        Console.WriteLine("  KeepBackups is {0}, so these {1} old backup(s) can go:", keep, old.Count);
        foreach (string dir in old) Console.WriteLine("    " + Path.GetFileName(dir));
        if (!AskInConsole("  Move them to the Recycle Bin? (y/n) ")) return;

        foreach (string dir in old)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                Console.WriteLine("  Moved to Recycle Bin: " + Path.GetFileName(dir));
            }
            catch (Exception ex)
            {
                WriteColored("  Could not remove " + Path.GetFileName(dir) + ": " + ex.Message, ConsoleColor.Yellow);
            }
        }
        Console.WriteLine();
    }

    class ScanResult
    {
        public List<FileInfo> Files = new List<FileInfo>();
        public List<string> Folders = new List<string>();
        public int SkippedFolders, SkippedFolderFiles, SkippedFiles;
        public long SkippedFolderBytes, SkippedFileBytes;
        // Skipped items without a leading "." (Linux and Windows leftovers), listed by name in the statistics
        public List<string> SkippedNamed = new List<string>();
    }

    // Linux disk-check (fsck.fat) recovered fragments: FSCK0000.REC, FSCK0001.REC, ...
    static readonly Regex FsckFile = new Regex(@"^FSCK\d{4}\.REC$", RegexOptions.IgnoreCase);

    // Leftovers other computers write to memory cards, which the M8 doesn't use:
    // - names starting with "." — macOS (.DS_Store, ._*, .Spotlight-V100, .fseventsd, .Trashes) and
    //   Linux (.Trash-1000, .directory, .hidden, .xdg-volume-info, .goutputstream-*, .fuse_hidden*)
    // - Linux: the lost+found folder, FSCK*.REC files, and editor backup files ending in "~"
    // - Windows: Thumbs.db, ehthumbs.db (thumbnail caches) and desktop.ini (folder view settings)
    static bool IsLeftover(string name, bool isFolder)
    {
        if (name.StartsWith(".")) return true;
        if (isFolder) return name == "lost+found";
        return name.EndsWith("~") || FsckFile.IsMatch(name) ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ehthumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    // Walks the tree, skipping Windows system folders and leftovers from other computers (see IsLeftover).
    static void CollectFiles(string dir, ScanResult scan)
    {
        try
        {
            foreach (FileInfo file in new DirectoryInfo(dir).GetFiles())
            {
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
            foreach (string sub in Directory.GetDirectories(dir))
            {
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
                            scan.SkippedFolderFiles++;
                            scan.SkippedFolderBytes += f.Length;
                        }
                    }
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
