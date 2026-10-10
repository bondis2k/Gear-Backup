// The checksum list saved in every backup (checksums.sha256), and checking backups against it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class M8Backup
{
    // Same format as the sha256sum tool: "<64 hex digits>  <path with forward slashes>", one file per line.
    const string ChecksumFileName = "checksums.sha256";

    // Files the program itself writes into a backup folder (not part of the M8's contents).
    static bool IsBackupMetaFile(string path)
    {
        string normalized = path.Replace('/', '\\');
        return normalized.EndsWith("\\.m8backup", StringComparison.OrdinalIgnoreCase) ||
               normalized.IndexOf("\\.m8backup\\", StringComparison.OrdinalIgnoreCase) >= 0 || IsStatisticsFile(path) ||
               Path.GetFileName(path).StartsWith(ChecksumFileName, StringComparison.OrdinalIgnoreCase);
    }

    // Reads a backup's checksum list: relative path (with backslashes) -> checksum. Null if it has none.
    static Dictionary<string, string> LoadChecksums(string folder)
    {
        string file = Path.Combine(folder, ChecksumFileName);
        if (!File.Exists(file)) return null;
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
        {
            // "<hash>  <path>" or "<hash> *<path>" (binary-mode marker)
            if (line.Length < 67 || line[64] != ' ') continue;
            string relative = line.Substring(66).Replace('/', '\\');
            SafePath(folder, relative);
            string hash = line.Substring(0, 64).ToLowerInvariant();
            if (!ValidHash(hash)) throw new IOException("Invalid checksum entry: " + relative);
            sums[relative] = hash;
        }
        return sums;
    }

    // Writes the checksum list for every file now in the folder. Checksums in `known` are used as they are;
    // any other file is read and hashed. Returns the number of files listed.
    static int WriteChecksums(string folder, Dictionary<string, string> known)
    {
        List<string> files = BackupFiles(folder);
        var text = new StringBuilder();
        int count = 0;
        foreach (string path in files)
        {
            OperationContext.Current.Progress(Stage.SavingChecksums, path, 0, 0);
            if (IsBackupMetaFile(path)) continue;
            string relative = path.Substring(folder.Length).TrimStart('\\');
            string hash;
            if (known == null || !known.TryGetValue(relative, out hash)) hash = HashFile(path);
            text.Append(hash).Append("  ").Append(relative.Replace('\\', '/')).Append('\n');
            count++;
        }

        // Write to a temporary file first, so an interrupted save never leaves a half-written list
        string file = Path.Combine(folder, ChecksumFileName);
        AtomicText(file, text.ToString());
        return count;
    }

    // ---- Check backups ----

    // Lets the user pick one backup, or all of them (the last entry), and checks them against their
    // checksum lists. Used by both the CLI and the GUI. Title is null if nothing was checked.
    static OperationResult RunCheck(List<string> backups)
    {
        if (backups.Count == 0)
        {
            WriteColored("  No backups found to check.", ConsoleColor.Yellow);
            return new OperationResult { ExitCode = 1 };
        }

        WriteColored("  Check backups for damaged files", ConsoleColor.Cyan);
        var newestFirst = new List<string>(backups);
        newestFirst.Reverse();
        var labels = new List<string>();
        foreach (string backup in newestFirst) labels.Add(Path.GetFileName(backup) + " [" + BackupStatus(backup) + "]");
        labels.Add("All " + newestFirst.Count + " backups in this history");
        int pick = AskItem("Check which backup?", labels);
        if (pick == 0)
        {
            OperationOutput.WriteLine("  Check cancelled.");
            return new OperationResult();
        }
        List<string> toCheck = pick == labels.Count ? newestFirst : new List<string> { newestFirst[pick - 1] };
        OperationOutput.WriteLine();
        return new IntegrityEngine().Run(toCheck, OperationContext.Current);
    }

    // Returns the exit code; title/summary are for the final notification (null if nothing was done).
    static int CheckBackups(List<string> backups, out string title, out string summary)
    {
        OperationResult result = RunCheck(backups);
        title = result.Title;
        summary = result.Summary;
        return result.ExitCode;
    }

    // verifiedLinks (optional) collects the hard-linked files found OK, so CheckSnapshots can skip
    // reading the same content again.
    static void CheckBackup(string backup, ref int ok, ref int damaged, ref int missing, ref int unchecked_,
                            Dictionary<string, string> verifiedLinks = null)
    {
        OperationOutput.WriteLine("  " + Path.GetFileName(backup));
        Dictionary<string, string> sums = LoadChecksums(backup);
        if (sums == null)
        {
            WriteColored("    No checksum list: this backup was made before checksums were saved.", ConsoleColor.Yellow);
            if (AskYesNo("Create one now from the files as they are?"))
            {
                int count = WriteChecksums(backup, null);
                OperationOutput.WriteLine("    Checksum list created for {0:N0} files. Future checks will compare against it.", count);
            }
            else
            {
                unchecked_++;
            }
            OperationOutput.WriteLine();
            return;
        }

        long totalBytes = 0, doneBytes = 0;
        foreach (string relative in sums.Keys)
        {
            var info = new FileInfo(Path.Combine(backup, relative));
            if (info.Exists) totalBytes += info.Length;
        }

        var problems = new List<string>();
        int done = 0;
        foreach (var entry in sums)
        {
            OperationContext.Current.CheckCancellation();
            string path = Path.Combine(backup, entry.Key);
            done++;
            if (!File.Exists(path))
            {
                missing++;
                problems.Add("missing: " + entry.Key);
                continue;
            }
            doneBytes += new FileInfo(path).Length;
            string hash;
            try { hash = HashFile(path); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { hash = null; problems.Add("unreadable: " + entry.Key + " (" + ex.Message + ")"); }
            if (hash == entry.Value)
            {
                ok++;
                string id = verifiedLinks == null ? null : SharedFileId(path);
                if (id != null) verifiedLinks[id] = hash;
            }
            else
            {
                damaged++;
                if (hash != null) problems.Add("damaged: " + entry.Key);
            }
            OperationContext.Current.Progress(Stage.CheckingBackup, entry.Key, doneBytes, totalBytes, done, sums.Count);
        }
        OperationOutput.WriteLine();

        // Files that were added to the folder by hand aren't in the list; just mention them
        int extra = 0;
        foreach (string path in BackupFiles(backup))
            if (!IsBackupMetaFile(path) && !sums.ContainsKey(path.Substring(backup.Length).TrimStart('\\'))) extra++;

        if (problems.Count == 0)
            WriteColored(string.Format("    All {0:N0} files OK.", sums.Count), ConsoleColor.Green);
        else
            foreach (string p in problems) WriteColored("    " + p, ConsoleColor.Red);
        if (extra > 0)
            OperationOutput.WriteLine("    {0:N0} file(s) in the folder aren't in the checksum list (added later by hand?), not checked.", extra);
        OperationOutput.WriteLine();
    }
}
