using System;
using System.Collections.Generic;
using System.IO;
using static FileCopy;
using static PathSafety;
using static Checksums;
using static Snapshots;
using static VersionCatalog;
using static Backups;
using static ChangeSummary;
using static BackupZip;
using static ConsoleUi;
using static DeviceSelection;
using static Workflow;

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
                BackupSetNames.Validate(incremental ? config.IncrementalBackupName : config.FullBackupName);
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
                // An incremental backup keeps the layout of the backup it updates; the setting applies to new full backups.
                if (incremental ? ContentStoreBackup.IsSingle(previous) : config.SingleContentStore)
                    return new ContentStoreBackup().Run(source, backupRoot, previous, incremental, config);
                var run = new BackupRun(source, backupRoot, previous, incremental, config);
                int code = run.Execute(out title, out summary);
                return new OperationResult { ExitCode = code, Title = title, Summary = summary, Elapsed = timer.Elapsed,
                    Counts = title == null ? null : run.ResultCounts, OutputFolder = title == null ? null : run.OutputFolder };
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
internal sealed class BackupRun
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
    // Output/recovery checksums; incremental runs seed these from SQLite or recovery records.
    Dictionary<string, string> sums;
    Dictionary<string, string> previousSums;
    Dictionary<string, CatalogEntry> inventory;
    bool refreshInventory;
    HashSet<string> knownVersions;
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
        if (pathProblem != null)
        {
            WriteColored("  " + pathProblem, ConsoleColor.Red);
            summary = pathProblem;
            return 1;
        }
        if (CanFinishUnchanged())
        {
            unchanged = scan.Files.Count;
            RecordSuccess();
            if (refreshInventory || !File.Exists(BackupDatabase.CatalogPath(dest))) BackupDatabase.TryRefresh(dest);
            title = AppIdentity.Name + " complete";
            summary = "No new or changed files. " + unchanged.ToString("N0") + " unchanged; backup is up to date.";
            OperationOutput.WriteLine("  " + summary);
            return 0;
        }
        if (!CheckSpace()) return 1;
        if (!Approve()) return 0;
        CopyFiles();
        SaveHistory();
        Report();
        BackupDatabase.TryRefresh(dest);
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
    string pathProblem;

    // Every path this run will create must fit Windows' limits: the copies, their folders, dated old
    // versions of changed files (up to 21 characters longer), history objects and the statistics file.
    string CheckPathLengths()
    {
        var check = new PathLengthCheck();
        string root = dest.TrimEnd('\\') + "\\";
        foreach (FileInfo file in scan.Files)
        {
            string target = root + Relative(file);
            bool mayKeepOldVersion = incremental && target.Length <= PathLengthCheck.MaxFile && File.Exists(target);
            check.File(target, mayKeepOldVersion ? 21 : 0);
        }
        foreach (string folder in scan.Folders) check.Folder(root + folder.Substring(source.Length));
        if (config.PointInTimeHistory)
            check.File(Path.Combine(AppIdentity.MetadataRoot(dest), "objects", new string('0', 64)), ".tmp".Length);
        check.File(root + "backup-statistics_" + started.ToString("yyyy-MM-dd_HHmmss_fffffff") + ".txt");
        check.File(Path.Combine(AppIdentity.MetadataRoot(dest), ".gearcatalog-" + new string('0', 32) + ".tmp"));
        return check.Problem("Choose a shorter backup location (for example D:\\Backups), or shorten folder names on the source.");
    }
    public string OutputFolder { get { return dest; } }
    public OperationCounts ResultCounts { get { return new OperationCounts { Copied = copied, Unchanged = unchanged,
        Removed = prunedVersions, Failed = failed + (zipFailed ? 1 : 0) }; } }

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
        pathProblem = CheckPathLengths();
        if (pathProblem != null) return;

        resuming = incremental && BackupStatus(dest).Unfinished;
        states = new FileState[scan.Files.Count];
        CatalogData indexed = incremental ? BackupDatabase.TryIncrementalInventory(previous) : null;
        refreshInventory = incremental && indexed == null;
        if (indexed != null)
        {
            inventory = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
            previousSums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in indexed.Files) if (!entry.IsDirectory)
            { inventory.Add(entry.Path, entry); previousSums.Add(entry.Path, entry.Hash); }
        }
        else previousSums = previous == null ? null : LoadChecksums(previous);
        Dictionary<string, string> oldSums = previousSums;
        var pendingObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // HardLinks=yes on NTFS: the history store shares the backup files' content instead of copying it.
        linkedHistory = config.PointInTimeHistory && config.HardLinks && SupportsHardLinks(backupRoot);
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
                : CompareInventory(file, relative, thoroughNow, out comparedHash);
            totalBytes += file.Length;
            if (!incremental || states[i] != FileState.Same) neededBytes += file.Length;
            if (incremental && states[i] == FileState.Same) skippedInPlan++;
            else plan.Add((!incremental ? "Copy: " : states[i] == FileState.New ? "New: " : "Changed: ") + relative);
            // Reserve conservatively when the hash is not already known. Do not read
            // the whole source just to estimate space; copying computes its identity.
            string hash = comparedHash;
            if (hash == null && !resuming && incremental && states[i] == FileState.Same && oldSums != null)
                oldSums.TryGetValue(relative, out hash);
            if (!config.PointInTimeHistory || linkedHistory) continue;
            if (hash == null) historyBytes += file.Length;
            else if (!File.Exists(ObjectPath(dest, hash)) && pendingObjects.Add(hash)) historyBytes += file.Length;
        }

        nameWarnings = CurrentDevice.NameWarnings ? CheckNames(scan, source, config.MaxNameLength) : new List<string>();
        if (nameWarnings.Count > 0)
            WriteColored("  " + nameWarnings.Count + " name(s) the " + CurrentDevice.Name + " may not handle well (listed in the statistics).", ConsoleColor.Yellow);
    }

    FileState CompareInventory(FileInfo file, string relative, bool thorough, out string hash)
    {
        string target = SafePath(previous, relative);
        hash = null;
        CatalogEntry entry;
        if (inventory == null || !inventory.TryGetValue(relative, out entry))
            return CompareContent(file, target, thorough, out hash);
        var actual = new FileInfo(target);
        if (!actual.Exists) return FileState.New;
        // SQLite supplies the baseline; live metadata guards against deleted or manually changed
        // backup files. Thorough comparison still reads both contents to catch silent damage.
        if (actual.Length != entry.Size || actual.LastWriteTimeUtc.Ticks != entry.ModifiedTicks ||
            entry.Result == "damaged" || entry.Result == "unreadable") return FileState.Changed;
        if (thorough || !UsableFileTime(file.LastWriteTimeUtc) || !UsableFileTime(actual.LastWriteTimeUtc))
            return CompareContent(file, target, thorough, out hash);
        if (file.Length != entry.Size || Math.Abs(file.LastWriteTimeUtc.Ticks - entry.ModifiedTicks) > TimeSpan.FromSeconds(2).Ticks)
            return FileState.Changed;
        hash = entry.Hash;
        return FileState.Same;
    }

    bool CheckSpace()
    {
        if (!config.PointInTimeHistory)
            OperationOutput.WriteLine("  Point-in-time history is off; backup files and checksums only, with no extra history content store.");
        else if (linkedHistory)
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

    // Only the ordinary completed, history-free case can skip housekeeping. A missing checksum,
    // source deletion, new folder, resume, retention request or snapshot request takes the normal path.
    bool CanFinishUnchanged()
    {
        if (!incremental || PreviewOnly || resuming || plan.Count != 0 || config.PointInTimeHistory ||
            config.KeepBackups > 0 || config.KeepOldVersions > 0 || config.Zip ||
            BackupStatus(dest) != Status.Complete || previousSums == null || previousSums.Count != scan.Files.Count)
            return false;
        if (!File.Exists(Path.Combine(dest, ChecksumFileName))) return false;
        string devicePath = Path.Combine(AppIdentity.MetadataRoot(dest), "device.txt");
        if (!File.Exists(devicePath) || new FileInfo(devicePath).Length > Records.DeviceLimit ||
            Records.ReadText(devicePath, Records.DeviceLimit, "Backup device record").Trim() != CurrentDevice.Id) return false;
        foreach (FileInfo file in scan.Files)
        {
            OperationContext.Current.CheckCancellation();
            if (!previousSums.ContainsKey(Relative(file))) return false;
        }
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in scan.Folders)
        {
            if (!Directory.Exists(SafePath(dest, folder.Substring(source.Length)))) return false;
            folders.Add(folder.Substring(source.Length));
        }
        // A lightweight inventory check still detects manual additions and deleted empty folders.
        // It does not sort files, hash them, or build cleanup/history records.
        int rootLength = PathSafety.Rooted(dest).Length;
        foreach (FileSystemInfo entry in BackupTraversal.BackupEntries(dest))
        {
            string relative = entry.FullName.Substring(rootLength);
            if (entry is DirectoryInfo ? !folders.Contains(relative) : !previousSums.ContainsKey(relative)) return false;
        }
        return true;
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
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in scan.Files) sizes[Relative(file)] = file.Length;
        bool approved = ShowPlan(resuming ? "Resume backup" : "Backup preview", plan, neededBytes, config, sizes);
        stopwatch.Start();
        return approved;
    }

    void CopyFiles()
    {
        OperationContext.Current.CheckCancellation();
        // Mark before the first mutation. A killed process leaves an incomplete status.
        Directory.CreateDirectory(dest);
        SetBackupStatus(dest, Status.Incomplete);
        BackupDatabase.BeginInventoryUpdate(dest);
        if (!incremental) BackupSetNames.Write(dest, config.FullBackupName);
        string devicePath = Path.Combine(AppIdentity.MetadataRoot(dest), "device.txt");
        AtomicText(devicePath, CurrentDevice.Id + "\n");
        foreach (string folder in scan.Folders)
            Directory.CreateDirectory(SafePath(dest, folder.Substring(source.Length)));

        sums = (incremental ? previousSums : null) ??
               new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        knownVersions = incremental ? KnownOldVersions(dest) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        if (config.PointInTimeHistory)
        {
            try
            {
                // Populate missing hashes for resumed files before creating history.
                foreach (FileInfo f in scan.Files)
                {
                    string rel = Relative(f);
                    if (!sums.ContainsKey(rel)) sums[rel] = HashFile(SafePath(dest, rel));
                }
                if (failed == 0) SaveSnapshot(dest, source, scan, sums, started, config.Verify, linkedHistory, incremental, incremental ? config.IncrementalBackupName : null);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                failed++;
                failures.Add("Point-in-time history: " + ex.Message);
                WriteColored("  Could not save point-in-time history: " + ex.Message, ConsoleColor.Red);
            }
        }
        if (failed == 0 && incremental && config.KeepOldVersions > 0)
        {
            var onM8 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in scan.Files) onM8.Add(Relative(f));
            var known = new HashSet<string>(knownVersions, StringComparer.OrdinalIgnoreCase);
            known.UnionWith(keptVersions);
            prunedVersions = PruneOldVersions(dest, config.KeepOldVersions, onM8, known);
        }

        // Checksum list for the whole backup folder (files without a known checksum are read and hashed)
        try
        {
            var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo file in scan.Files) sourceNames.Add(Relative(file));
            SaveOldVersionRecord(dest, knownVersions, keptVersions, sourceNames);
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
                throw new IOException("Previous backup is missing its checksum inventory. Check or repair it before comparing backups.");
            states[i] = newHash == oldHash ? FileState.Same : FileState.Changed;
        }
    }

    void Report()
    {
        stopwatch.Stop();
        var report = new BackupReport {
            Source = source, Destination = dest, Previous = previous, Incremental = incremental,
            Copied = copied, Updated = updated, Unchanged = unchanged, CopiedBytes = copiedBytes,
            Started = started, Elapsed = stopwatch.Elapsed, Config = config, Scan = scan,
            ChecksumStatus = checksumStatus, LargestFile = largest, Changes = changes,
            PerFolder = perFolder, NameWarnings = nameWarnings, KeptVersions = keptVersions,
            PrunedVersions = prunedVersions, Failures = failures, Failed = failed
        };
        report.Write();
        SetBackupStatus(dest, failed == 0 ? Status.Complete : Status.Failed);
        if (failed == 0) RecordSuccess();
        WriteColored("  " + report.StatusText, failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
        OperationOutput.WriteLine();
    }

    void RecordSuccess()
    {
        try
        {
            string successPath = Path.Combine(AppIdentity.MetadataRoot(dest), "last-success.txt");
            AtomicText(successPath, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { OperationOutput.WriteLine("  Could not save dashboard completion time: " + ex.Message); }
    }
}
