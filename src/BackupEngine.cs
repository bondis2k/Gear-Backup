using System;
using System.Collections.Generic;
using System.IO;

partial class M8Backup
{
    internal sealed class BackupEngine
    {
        public OperationResult Run(string source, string backupRoot, string previous, bool incremental, Config config, OperationContext context)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool previousProgressMode = context.OverallProgressOnly;
            using (context.Activate())
            {
                try
                {
                    context.OverallProgressOnly = !incremental;
                    context.CheckCancellation();
                    source = Rooted(source);
                    backupRoot = Path.GetFullPath(backupRoot);
                    if (!Directory.Exists(source)) throw new IOException("Source folder does not exist: " + source);
                    RejectRootLinks(source);
                    RejectRootLinks(backupRoot);
                    if (Within(source, backupRoot) || Within(backupRoot, source))
                        throw new IOException("The backup folder and source must not contain one another.");
                    if (previous != null)
                    {
                        previous = Path.GetFullPath(previous);
                        if (!Within(previous, backupRoot) || Rooted(previous) == Rooted(backupRoot))
                            throw new IOException("The previous backup must belong to the selected history.");
                        RejectRootLinks(previous);
                    }
                    if (incremental && (previous == null || !Directory.Exists(previous)))
                        throw new IOException("An incremental backup needs an existing backup folder.");
                    string title, summary;
                    int code = RunCore(source, backupRoot, previous, incremental, config, out title, out summary);
                    return new OperationResult { ExitCode = code, Title = title, Summary = summary, Elapsed = timer.Elapsed };
                }
                catch (OperationCanceledException)
                {
                    return new OperationResult { ExitCode = 0, Cancelled = true, Title = "M8 backup cancelled",
                        Summary = "Backup cancelled. Completed copies remain available; incomplete backups can be resumed.", Elapsed = timer.Elapsed };
                }
                finally { context.OverallProgressOnly = previousProgressMode; }
            }
        }

        int RunCore(string source, string backupRoot, string previous, bool incremental, Config config,
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
                dest = Path.Combine(backupRoot, started.ToString("yyyy-MM-dd"));
                if (Directory.Exists(dest))
                    dest = UniqueFolder(backupRoot, started.ToString("yyyy-MM-dd_HHmmss_fffffff"));
            }

            OperationOutput.WriteLine((incremental ? "  Updating " : "  Backing up to ") + dest);
            OperationOutput.WriteLine();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var scan = new ScanResult();
            CollectFiles(source, scan);

            // Compare every file with the last backup: tells what changed, and what an incremental run must copy
            bool resuming = incremental && (BackupStatus(dest) == "incomplete" || BackupStatus(dest) == "failed");
            var states = new FileState[scan.Files.Count];
            var plan = new List<string>();
            int skippedInPlan = 0;
            Dictionary<string, string> oldSums = previous == null ? null : LoadChecksums(previous);
            var pendingObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0, neededBytes = 0;
            long historyBytes = 0;
            for (int i = 0; i < scan.Files.Count; i++)
            {
                OperationContext.Current.Progress("Planning backup", scan.Files[i].FullName, 0, 0, i, scan.Files.Count);
                FileInfo file = scan.Files[i];
                string comparedHash = null;
                states[i] = previous == null
                    ? FileState.New
                    : CompareContent(file, SafePath(previous, file.FullName.Substring(source.Length)), config.Thorough, out comparedHash);
                totalBytes += file.Length;
                if (!incremental || states[i] != FileState.Same) neededBytes += file.Length;
                string relative = file.FullName.Substring(source.Length);
                if (incremental && states[i] == FileState.Same) skippedInPlan++;
                else plan.Add((!incremental ? "Copy: " : states[i] == FileState.New ? "New: " : "Changed: ") + relative);
                // Reserve conservatively when the hash is not already known. Do not read
                // the whole source just to estimate space; copying computes its identity.
                string hash = comparedHash;
                if (hash == null && !resuming && incremental && states[i] == FileState.Same && oldSums != null)
                    oldSums.TryGetValue(relative, out hash);
                if (hash == null) historyBytes += file.Length;
                else if (!File.Exists(ObjectPath(dest, hash)) && pendingObjects.Add(hash)) historyBytes += file.Length;
            }
            Changes changes = FindChanges(scan, source, states, previous);

            List<string> nameWarnings = CheckNames(scan, source, config.MaxNameLength);
            if (nameWarnings.Count > 0)
                WriteColored("  " + nameWarnings.Count + " name(s) the M8 may not handle well (listed in the statistics).", ConsoleColor.Yellow);

            OperationOutput.WriteLine("  History store needs up to {0} additional space (identical content is shared).", FormatSize(historyBytes));
            string spaceError = CheckFreeSpace(backupRoot, neededBytes + historyBytes);
            if (spaceError != null)
            {
                WriteColored("  " + spaceError, ConsoleColor.Red);
                return 1;
            }

            if (incremental) OperationOutput.WriteLine("  Unchanged files skipped: {0:N0}.", skippedInPlan);
            if (incremental && plan.Count == 0 && !PreviewOnly)
                OperationOutput.WriteLine("  No new or changed files to copy.");
            else
            {
                // Time spent waiting for a decision is not backup work.
                stopwatch.Stop();
                bool approved = ShowPlan(resuming ? "Resume backup" : "Backup preview", plan, neededBytes, config);
                stopwatch.Start();
                if (!approved) return 0;
            }
            OperationContext.Current.CheckCancellation();
            // Mark before the first mutation. A killed process leaves an incomplete status.
            Directory.CreateDirectory(dest);
            SetBackupStatus(dest, "incomplete");
            foreach (string folder in scan.Folders)
                Directory.CreateDirectory(SafePath(dest, folder.Substring(source.Length)));
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

            PrintProgress(0, totalBytes, 0, scan.Files.Count, 0);

            for (int i = 0; i < scan.Files.Count; i++)
            {
                OperationContext.Current.CheckCancellation();
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
                        bool suspect = (config.Verify && hasOldHash && HashFile(target) != expectedOld) || (resuming && !hasOldHash);
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
                    if (ex is OperationCanceledException) throw;
                    failed++;
                    string message = relative + " (" + ex.Message + ")";
                    failures.Add(message);
                    OperationOutput.WriteLine();
                    WriteColored("  Failed: " + message, ConsoleColor.Red);
                }
                PrintProgress(processedBytes, totalBytes, copied + unchanged, scan.Files.Count, copiedBytes);
            }
            OperationOutput.WriteLine();
            OperationOutput.WriteLine();

            int prunedVersions = 0;
            try
            {
                // Populate missing hashes for verified resume/legacy files before creating history.
                foreach (FileInfo f in scan.Files)
                {
                    string rel = f.FullName.Substring(source.Length);
                    if (!sums.ContainsKey(rel)) sums[rel] = HashFile(SafePath(dest, rel));
                }
                if (failed == 0) SaveSnapshot(dest, source, scan, sums, started, config.Verify);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
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
                OperationOutput.WriteLine("  Saving checksum list...");
                int listed = WriteChecksums(dest, sums);
                checksumStatus = string.Format("{0:N0} files listed in {1}", listed, ChecksumFileName);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                failed++;
                failures.Add("Checksum list: " + ex.Message);
                checksumStatus = "could not be saved: " + ex.Message;
                WriteColored("  Checksum list " + checksumStatus, ConsoleColor.Yellow);
            }
            OperationOutput.WriteLine();

            // Statistics, written to both the console and a text file in the backup folder
            stopwatch.Stop();
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

            report.WriteLine("  Free space on M8:       {0}", DriveSpaceText(Path.GetPathRoot(source)));
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
            OperationOutput.Write(report.ToString());

            // An incremental run gets its own dated file so the earlier statistics are kept
            string statsFile = Path.Combine(dest, incremental
                ? "backup-statistics_" + started.ToString("yyyy-MM-dd_HHmmss_fffffff") + ".txt"
                : "backup-statistics.txt");
            try
            {
                File.WriteAllText(statsFile, "  M8 " + (incremental ? "incremental " : "") + "backup statistics\r\n" + report + "  " + status + "\r\n");
                OperationOutput.WriteLine("  Statistics saved to " + statsFile);
            }
            catch (Exception ex)
            {
                WriteColored("  Could not save statistics file: " + ex.Message, ConsoleColor.Yellow);
            }
            OperationOutput.WriteLine();

            SetBackupStatus(dest, failed == 0 ? "complete" : "failed");
            WriteColored("  " + status, failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
            OperationOutput.WriteLine();

            // Only tidy up old backups once this one is known to be good
            if (failed == 0 && config.KeepBackups > 0)
                PruneBackups(backupRoot, config.KeepBackups, dest);

            title = failed == 0 ? "M8 backup complete" : "M8 backup finished with errors";
            summary = string.Format("{0:N0} files, {1} copied in {2}.", copied, FormatSize(copiedBytes), FormatTime(stopwatch.Elapsed));
            if (failed > 0) summary += " " + failed + " file(s) failed.";
            summary += ShortSongSummary(changes);
            return failed == 0 ? 0 : 2;
        }
    }

}
