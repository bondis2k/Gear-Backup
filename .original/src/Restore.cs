// Restoring a whole backup, or a single file, to the M8.
using System;
using System.Collections.Generic;
using System.IO;

partial class M8Backup
{
    static int Restore(string m8, List<string> backups, Config config, out string title, out string summary)
    {
        title = summary = null;
        if (backups.Count == 0)
        {
            WriteColored("  No backups found to restore from.", ConsoleColor.Yellow);
            return 1;
        }

        WriteColored("  Restore to the M8", ConsoleColor.Cyan);
        var newestFirst = new List<string>(backups);
        newestFirst.Reverse();
        if (newestFirst.Count > 20) newestFirst.RemoveRange(20, newestFirst.Count - 20);
        for (int i = 0; i < newestFirst.Count; i++)
            Console.WriteLine("    {0,2}) {1}", i + 1, Path.GetFileName(newestFirst[i]));
        int pick = AskNumber("  Restore from which backup? Number, or Enter to cancel: ", newestFirst.Count);
        if (pick == 0)
        {
            Console.WriteLine("  Restore cancelled.");
            return 0;
        }
        string backup = newestFirst[pick - 1];

        // Each item: [file in the backup, path relative to the M8 root]
        var items = new List<string[]>();
        bool everything = AskKey("  Restore [A]ll files, or a [S]ingle file (song, instrument, sample...)? ", "as") == 'a';
        if (everything)
        {
            foreach (string path in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
            {
                if (IsBackupMetaFile(path) || IsOldVersion(path)) continue;
                items.Add(new[] { path, path.Substring(backup.Length).TrimStart('\\') });
            }
        }
        else
        {
            string chosen = PickFile(backup);
            if (chosen == null)
            {
                Console.WriteLine("  Restore cancelled.");
                return 0;
            }
            string relative = chosen.Substring(backup.Length).TrimStart('\\');
            if (IsOldVersion(relative))
            {
                string original = OriginalName(relative);
                Console.WriteLine("  This is an old version of " + original);
                if (AskKey("  Restore it under the [O]riginal name, or keep the [D]ated name? ", "od") == 'o')
                    relative = original;
            }
            items.Add(new[] { chosen, relative });
        }

        // Work out what actually needs copying
        var toCopy = new List<string[]>();
        int differing = 0, newerOnM8 = 0, identical = 0;
        foreach (string[] item in items)
        {
            var file = new FileInfo(item[0]);
            string target = Path.Combine(m8, item[1]);
            FileState state = Compare(file, target);
            if (state == FileState.Same) { identical++; continue; }
            if (state == FileState.Changed)
            {
                differing++;
                if (File.GetLastWriteTimeUtc(target) > file.LastWriteTimeUtc.AddSeconds(2)) newerOnM8++;
            }
            toCopy.Add(item);
        }
        if (toCopy.Count == 0)
        {
            WriteColored("  Nothing to restore: the M8 already has " + (items.Count == 1 ? "this file." : "all these files."), ConsoleColor.Green);
            return 0;
        }

        Console.WriteLine("  From {0}: {1:N0} file(s) missing on the M8, {2:N0} that differ, {3:N0} already identical.",
                          Path.GetFileName(backup), toCopy.Count - differing, differing, identical);
        if (differing > 0)
        {
            if (newerOnM8 > 0)
                WriteColored("  " + newerOnM8 + " of the M8's files are newer than the backup. Back them up first if you want to keep them.",
                             ConsoleColor.Yellow);
            char answer = AskKey("  [R]eplace the M8's versions, [K]eep them and copy only missing files, or [C]ancel? ", "rkc");
            if (answer == 'c')
            {
                Console.WriteLine("  Restore cancelled.");
                return 0;
            }
            if (answer == 'k')
            {
                toCopy.RemoveAll(item => File.Exists(Path.Combine(m8, item[1])));
                if (toCopy.Count == 0)
                {
                    Console.WriteLine("  Nothing missing on the M8, so nothing was copied.");
                    return 0;
                }
            }
        }
        else if (!AskInConsole("  Copy " + toCopy.Count + " file(s) to the M8? (y/n) "))
        {
            Console.WriteLine("  Restore cancelled.");
            return 0;
        }

        // Each replaced file briefly exists twice (new copy written first), so allow for the largest one
        long needed = 0, largestReplaced = 0, totalBytes = 0;
        foreach (string[] item in toCopy)
        {
            long size = new FileInfo(item[0]).Length;
            totalBytes += size;
            needed += size;
            var existing = new FileInfo(Path.Combine(m8, item[1]));
            if (existing.Exists)
            {
                needed -= existing.Length;
                largestReplaced = Math.Max(largestReplaced, size);
            }
        }
        string spaceError = CheckFreeSpace(m8, Math.Max(0, needed) + largestReplaced);
        if (spaceError != null)
        {
            WriteColored("  " + spaceError, ConsoleColor.Red);
            return 1;
        }

        Console.WriteLine();
        if (everything)
            foreach (string dir in Directory.GetDirectories(backup, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(m8, dir.Substring(backup.Length).TrimStart('\\')));

        // The backup's checksum list, to make sure no damaged file is copied back to the M8
        Dictionary<string, string> sums = LoadChecksums(backup);
        if (sums == null)
            WriteColored("  This backup has no checksum list, so its files can't be checked for damage before restoring.",
                         ConsoleColor.Yellow);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int copied = 0, failed = 0;
        long copiedBytes = 0, processedBytes = 0;
        var failures = new List<string>();
        foreach (string[] item in toCopy)
        {
            var file = new FileInfo(item[0]);
            string target = Path.Combine(m8, item[1]);
            string temp = target + ".restoring";
            processedBytes += file.Length;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                // A file being replaced gets the new copy written next to it first,
                // so a failed or damaged copy never loses the M8's file
                bool replacing = File.Exists(target);
                string written = replacing ? temp : target;
                TryDelete(temp);
                string hash = CopyFile(file, written, config.Verify);

                string expected;
                if (sums != null && sums.TryGetValue(item[0].Substring(backup.Length).TrimStart('\\'), out expected) &&
                    expected != hash)
                {
                    TryDelete(written);
                    throw new IOException("the backup copy is damaged: its checksum differs from when it was backed up");
                }
                if (replacing)
                {
                    File.Delete(target);
                    File.Move(temp, target);
                }
                copied++;
                copiedBytes += file.Length;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                failed++;
                string message = item[1] + " (" + ex.Message + ")";
                failures.Add(message);
                Console.WriteLine();
                WriteColored("  Failed: " + message, ConsoleColor.Red);
            }
            PrintProgress(processedBytes, totalBytes, copied + failed, toCopy.Count, copiedBytes);
        }
        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine();

        summary = string.Format("{0:N0} file(s), {1} restored from {2} in {3}.", copied, FormatSize(copiedBytes),
                                Path.GetFileName(backup), FormatTime(stopwatch.Elapsed));
        if (failed > 0)
        {
            summary += " " + failed + " file(s) failed.";
            WriteColored("  Failed files (" + failed + "):", ConsoleColor.Yellow);
            foreach (string f in failures) Console.WriteLine("    " + f);
        }
        WriteColored("  " + summary, failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
        Console.WriteLine();
        title = failed == 0 ? "M8 restore complete" : "M8 restore finished with errors";
        return failed == 0 ? 0 : 2;
    }

    // Lets the user search a backup by name and pick one file. Returns null if cancelled.
    static string PickFile(string backup)
    {
        var all = new List<string>();
        foreach (string path in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
            if (!IsBackupMetaFile(path)) all.Add(path);
        all.Sort(StringComparer.OrdinalIgnoreCase);

        const int maxShown = 40;
        while (true)
        {
            Console.Write("  Type part of the file name (Enter = list all songs, q = cancel): ");
            string filter = Console.ReadLine();
            if (filter == null || filter.Trim().ToLowerInvariant() == "q") return null;
            filter = filter.Trim();

            var matches = all.FindAll(path => filter.Length == 0
                ? IsSong(path)
                : Path.GetFileName(path).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            if (matches.Count == 0)
            {
                Console.WriteLine("  No matching files.");
                continue;
            }
            if (matches.Count > maxShown)
            {
                Console.WriteLine("  {0} files match; showing the first {1}. Type more of the name to narrow it down.", matches.Count, maxShown);
                matches.RemoveRange(maxShown, matches.Count - maxShown);
            }
            for (int i = 0; i < matches.Count; i++)
            {
                var info = new FileInfo(matches[i]);
                Console.WriteLine("    {0,2}) {1}  ({2}, {3:yyyy-MM-dd HH:mm})", i + 1,
                                  matches[i].Substring(backup.Length).TrimStart('\\'), FormatSize(info.Length), info.LastWriteTime);
            }
            int pick = AskNumber("  Restore which file? Number, or Enter to search again: ", matches.Count);
            if (pick > 0) return matches[pick - 1];
        }
    }
}
