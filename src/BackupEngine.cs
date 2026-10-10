using System;
using System.Collections.Generic;
using System.IO;

partial class GearBackup
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
                    int code = new BackupRun(source, backupRoot, previous, incremental, config).Execute(out title, out summary);
                    return new OperationResult { ExitCode = code, Title = title, Summary = summary, Elapsed = timer.Elapsed };
                }
                catch (OperationCanceledException)
                {
                    return new OperationResult { ExitCode = 0, Cancelled = true, Title = AppIdentity.Name + " cancelled",
                        Summary = "Backup cancelled. Completed copies remain available; incomplete backups can be resumed.", Elapsed = timer.Elapsed };
                }
                finally { context.OverallProgressOnly = previousProgressMode; }
            }
        }
    }

    // One backup run, in steps: plan, check space and approval, copy, save history and checksums, report.
    // The steps share the run's state through these fields.
    sealed class BackupRun
    {
        readonly string source, backupRoot, previous;
        readonly bool incremental;
        readonly Config config;
        readonly DateTime started = DateTime.Now;
        readonly System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        string dest;
        bool resuming, linkedHistory;

        // Planning
        ScanResult scan;
        FileState[] states;
        readonly List<string> plan = new List<string>();
        int skippedInPlan;
        long totalBytes, neededBytes, historyBytes;
        List<string> nameWarnings;

        // Copying
        int copied, failed, updated, unchanged, prunedVersions;
        long copiedBytes;
        FileInfo largest;
        readonly List<string> failures = new List<string>();
        readonly List<string> keptVersions = new List<string>();
        // Per top-level folder (Songs, Samples, ...): [files, bytes]
        readonly SortedDictionary<string, long[]> perFolder = new SortedDictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        // Checksums for the backup's checksum list; an incremental run starts from the existing list
        Dictionary<string, string> sums;
        Changes changes;
        string checksumStatus;

        public BackupRun(string source, string backupRoot, string previous, bool incremental, Config config)
        {
            this.source = source;
            this.backupRoot = backupRoot;
            this.previous = previous;
            this.incremental = incremental;
            this.config = config;
        }

        public int Execute(out string title, out string summary)
        {
            title = summary = null;
            ChooseDestination();
            stopwatch.Start();
            Plan();
            if (!CheckSpace()) return 1;
            if (!Approve()) return 0;
            CopyFiles();
            SaveHistory();
            Report();
            string zipPath = SaveZip();

            // Only tidy up old backups once this one is known to be good
            if (failed == 0 && config.KeepBackups > 0)
                PruneBackups(backupRoot, config.KeepBackups, dest);

            title = AppIdentity.Name + (failed == 0 ? " complete" : " finished with errors");
            summary = string.Format("{0:N0} files, {1} copied in {2}.", copied, FormatSize(copiedBytes), FormatTime(stopwatch.Elapsed));
            if (failed > 0) summary += " " + failed + " file(s) failed.";
            summary += ShortSongSummary(changes);
            if (zipPath != null) summary += " Zip: " + Path.GetFileName(zipPath) + ".";
            if (zipFailed) summary += " The zip could not be saved.";
            return failed == 0 && !zipFailed ? 0 : 2;
        }

        bool zipFailed;

        // --zip: after a successful full backup, also save it as a zip next to its folder.
        // Returns the zip's path, or null when none was made.
        string SaveZip()
        {
            if (!config.Zip) return null;
            if (incremental)
            {
                OperationOutput.WriteLine("  --zip applies to full backups; no zip made for this incremental backup.");
                return null;
            }
            if (failed > 0)
            {
                WriteColored("  No zip made, because some files failed to back up.", ConsoleColor.Yellow);
                return null;
            }
            try
            {
                OperationOutput.WriteLine("  Saving zip...");
                string zipPath = ZipBackup(dest);
                OperationOutput.WriteLine();
                OperationOutput.WriteLine("  Zip saved to " + zipPath);
                OperationOutput.WriteLine();
                return zipPath;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                zipFailed = true;
                WriteColored("  Could not save the zip: " + ex.Message, ConsoleColor.Yellow);
                return null;
            }
        }

        string Relative(FileInfo file) { return file.FullName.Substring(source.Length); }

        void ChooseDestination()
        {
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
        }

        // Scans the source and compares every file with the last backup: tells what changed,
        // what an incremental run must copy, and how much space is needed.
        void Plan()
        {
            scan = new ScanResult();
            CollectFiles(source, scan);

            resuming = incremental && Status.Unfinished(BackupStatus(dest));
            states = new FileState[scan.Files.Count];
            Dictionary<string, string> oldSums = previous == null ? null : LoadChecksums(previous);
            var pendingObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // HardLinks=yes on NTFS: the history store shares the backup files' content instead of copying it.
            linkedHistory = config.HardLinks && SupportsHardLinks(backupRoot);
            // A full backup copies everything anyway, so with Thorough=yes its content comparison (only used
            // for the change summary) waits for the checksums computed while copying: the card is read once.
            bool thoroughNow = config.Thorough && incremental;
            for (int i = 0; i < scan.Files.Count; i++)
            {
                OperationContext.Current.Progress(Stage.PlanningBackup, scan.Files[i].FullName, 0, 0, i, scan.Files.Count);
                FileInfo file = scan.Files[i];
                string relative = Relative(file);
                string comparedHash = null;
                states[i] = previous == null
                    ? FileState.New
                    : CompareContent(file, SafePath(previous, relative), thoroughNow, out comparedHash);
                totalBytes += file.Length;
                if (!incremental || states[i] != FileState.Same) neededBytes += file.Length;
                if (incremental && states[i] == FileState.Same) skippedInPlan++;
                else plan.Add((!incremental ? "Copy: " : states[i] == FileState.New ? "New: " : "Changed: ") + relative);
                // Reserve conservatively when the hash is not already known. Do not read
                // the whole source just to estimate space; copying computes its identity.
                string hash = comparedHash;
                if (hash == null && !resuming && incremental && states[i] == FileState.Same && oldSums != null)
                    oldSums.TryGetValue(relative, out hash);
                if (linkedHistory) continue;
                if (hash == null) historyBytes += file.Length;
                else if (!File.Exists(ObjectPath(dest, hash)) && pendingObjects.Add(hash)) historyBytes += file.Length;
            }

            nameWarnings = CurrentDevice.NameWarnings ? CheckNames(scan, source, config.MaxNameLength) : new List<string>();
            if (nameWarnings.Count > 0)
                WriteColored("  " + nameWarnings.Count + " name(s) the " + CurrentDevice.Name + " may not handle well (listed in the statistics).", ConsoleColor.Yellow);
        }

        bool CheckSpace()
        {
            if (linkedHistory)
                OperationOutput.WriteLine("  History store shares the backup files through hard links; no extra space needed.");
            else
                OperationOutput.WriteLine("  History store needs up to {0} additional space (identical content is shared).", FormatSize(historyBytes));
            // A zip can be up to the card's size (WAV samples barely compress).
            long zipBytes = config.Zip && !incremental ? totalBytes : 0;
            if (zipBytes > 0) OperationOutput.WriteLine("  The zip needs up to {0}.", FormatSize(zipBytes));
            string spaceError = CheckFreeSpace(backupRoot, neededBytes + historyBytes + zipBytes);
            if (spaceError == null) return true;
            WriteColored("  " + spaceError, ConsoleColor.Red);
            return false;
        }

        bool Approve()
        {
            if (incremental) OperationOutput.WriteLine("  Unchanged files skipped: {0:N0}.", skippedInPlan);
            if (incremental && plan.Count == 0 && !PreviewOnly)
            {
                OperationOutput.WriteLine("  No new or changed files to copy.");
                return true;
            }
            // Time spent waiting for a decision is not backup work.
            stopwatch.Stop();
            bool approved = ShowPlan(resuming ? "Resume backup" : "Backup preview", plan, neededBytes, config);
            stopwatch.Start();
            return approved;
        }

        void CopyFiles()
        {
            OperationContext.Current.CheckCancellation();
            // Mark before the first mutation. A killed process leaves an incomplete status.
            Directory.CreateDirectory(dest);
            SetBackupStatus(dest, Status.Incomplete);
            foreach (string folder in scan.Folders)
                Directory.CreateDirectory(SafePath(dest, folder.Substring(source.Length)));

            sums = (incremental ? LoadChecksums(dest) : null) ??
                   new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long processedBytes = 0;
            PrintProgress(0, totalBytes, 0, scan.Files.Count, 0);

            for (int i = 0; i < scan.Files.Count; i++)
            {
                OperationContext.Current.CheckCancellation();
                FileInfo file = scan.Files[i];
                string relative = Relative(file);
                string target = SafePath(dest, relative);
                processedBytes += file.Length;
                if (incremental && states[i] == FileState.Same)
                {
                    if (resuming) sums[relative] = HashFile(target);
                    unchanged++;
                }
                else CopyOne(file, relative, target, states[i]);
                PrintProgress(processedBytes, totalBytes, copied + unchanged, scan.Files.Count, copiedBytes);
            }
            OperationOutput.WriteLine();
            OperationOutput.WriteLine();
        }

        void CopyOne(FileInfo file, string relative, string target, FileState state)
        {
            string oldVersion = null;
            try
            {
                if (incremental && state == FileState.Changed)
                {
                    // Changed on the M8: keep the old copy, renamed with its own modified date and time
                    string expectedOld;
                    bool hasOldHash = sums.TryGetValue(relative, out expectedOld);
                    bool suspect = (config.Verify && hasOldHash && HashFile(target) != expectedOld) || (resuming && !hasOldHash);
                    if (suspect)
                    {
                        oldVersion = SafePath(Path.Combine(AppIdentity.MetadataRoot(dest), "quarantine", Guid.NewGuid().ToString("N")), relative);
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
        }

        // Point-in-time snapshot, old-version pruning and the checksum list.
        void SaveHistory()
        {
            if (config.Thorough && !incremental && previous != null) RefineByChecksums();
            changes = FindChanges(scan, source, states, previous);

            try
            {
                // Populate missing hashes for verified resume/legacy files before creating history.
                foreach (FileInfo f in scan.Files)
                {
                    string rel = Relative(f);
                    if (!sums.ContainsKey(rel)) sums[rel] = HashFile(SafePath(dest, rel));
                }
                if (failed == 0) SaveSnapshot(dest, source, scan, sums, started, config.Verify, linkedHistory);
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
                foreach (FileInfo f in scan.Files) onM8.Add(Relative(f));
                prunedVersions = PruneOldVersions(dest, config.KeepOldVersions, onM8);
            }

            // Checksum list for the whole backup folder (files without a known checksum are read and hashed)
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
        }

        // Full backup with Thorough=yes: compare content using the checksums from copying and the previous
        // backup's checksum list, instead of hashing the card and the previous backup before copying.
        void RefineByChecksums()
        {
            Dictionary<string, string> oldSums = LoadChecksums(previous);
            for (int i = 0; i < scan.Files.Count; i++)
            {
                if (states[i] == FileState.New) continue;
                string relative = Relative(scan.Files[i]);
                string newHash, oldHash;
                if (!sums.TryGetValue(relative, out newHash)) continue;  // copy failed; keep the metadata result
                if (oldSums == null || !oldSums.TryGetValue(relative, out oldHash))
                {
                    OperationContext.Current.Progress(Stage.Comparing, relative, 0, 0);
                    oldHash = HashFile(SafePath(previous, relative));  // older backup without a checksum list
                }
                states[i] = newHash == oldHash ? FileState.Same : FileState.Changed;
            }
        }

        // Statistics, written to both the console and a text file in the backup folder; sets the final status.
        void Report()
        {
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
            report.WriteLine("  Folders:       {0:N0}", scan.Folders.Count);
            report.WriteLine("  Data copied:   {0}", FormatSize(copiedBytes));
            report.WriteLine("  Time:          {0}", FormatTime(stopwatch.Elapsed));
            report.WriteLine("  Average speed: {0}/s", FormatSize((long)(copiedBytes / seconds)));
            report.WriteLine("  Verified:      {0}", config.Verify
                ? "yes (each copy read back and its SHA-256 checksum compared)"
                : "no (Verify=no in " + AppIdentity.ConfigFileName + ")");
            report.WriteLine("  Checksums:     {0}", checksumStatus);
            if (largest != null)
                report.WriteLine("  Largest file:  {0} ({1})", Relative(largest), FormatSize(largest.Length));
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

            string spaceLabel = "Free space on " + CurrentDevice.Name + ":";
            // Same column as before for the M8; longer device names still get a gap before the value.
            report.WriteLine("  {0}{1}", spaceLabel.PadRight(Math.Max(24, spaceLabel.Length + 2)),
                CurrentDevice.Mtp ? "not available over MTP" : DriveSpaceText(Path.GetPathRoot(source)));
            string destRoot = Path.GetPathRoot(dest);
            report.WriteLine("  Free space left on {0}  {1}", destRoot, FreeSpaceText(destRoot));
            report.WriteLine();

            if (nameWarnings.Count > 0)
            {
                report.WriteLine("  Names the {0} may not handle well ({1}):", CurrentDevice.Name, nameWarnings.Count);
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
                File.WriteAllText(statsFile, "  " + AppIdentity.Name + " " + (incremental ? "incremental " : "") + "statistics\r\n" + report + "  " + status + "\r\n");
                OperationOutput.WriteLine("  Statistics saved to " + statsFile);
            }
            catch (Exception ex)
            {
                WriteColored("  Could not save statistics file: " + ex.Message, ConsoleColor.Yellow);
            }
            OperationOutput.WriteLine();

            SetBackupStatus(dest, failed == 0 ? Status.Complete : Status.Failed);
            WriteColored("  " + status, failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
            OperationOutput.WriteLine();
        }
    }

}
