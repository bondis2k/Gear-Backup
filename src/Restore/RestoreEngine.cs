using System;
using System.Collections.Generic;
using System.IO;
using static FileCopy;
using static PathSafety;
using static Snapshots;
using static Backups;
using static RestoreFlow;
using static ConsoleUi;
using static Formatting;
using static AtomicFile;
using static PlanReview;
using static SourceScan;

// A restore runs in two halves: RunSelected plans it (compares with the target, asks about differing
// files, shows the preview), then Run carries out the approved plan.
internal sealed class RestoreEngine
{
    public OperationResult RunSelected(string targetRoot, Snapshot snapshot, List<RestoreEntry> items, RestoreMode mode,
        string bundle, bool exact, Config config, OperationContext context, Action beforeRestore = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (context.Activate())
        {
            try
            {
                context.CheckCancellation();
                targetRoot = Rooted(targetRoot);
                RejectRootLinks(targetRoot);
                OperationResult result = PlanRestore(targetRoot, snapshot, items, mode, bundle, exact, config, beforeRestore);
                result.Cancelled |= context.Cancellation.IsCancellationRequested;
                result.Elapsed = timer.Elapsed;
                return result;
            }
            catch (OperationCanceledException)
            {
                return new OperationResult { ExitCode = 0, Cancelled = true, Title = AppIdentity.Name + " restore cancelled",
                    Summary = "Restore planning cancelled; target files were not changed.", Elapsed = timer.Elapsed };
            }
        }
    }

    public OperationResult Run(string targetRoot, List<RestoreEntry> copies, List<string> removals, List<string> folders,
        List<string> extraFolders, Dictionary<string, string> targetHashes, Config config, OperationContext context)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (context.Activate())
        {
            try
            {
                context.CheckCancellation();
                targetRoot = Rooted(targetRoot);
                RejectRootLinks(targetRoot);
                OperationResult result = ExecuteCore(targetRoot, copies, removals, folders, extraFolders, targetHashes, config);
                result.Elapsed = timer.Elapsed;
                return result;
            }
            catch (OperationCanceledException)
            {
                return new OperationResult { ExitCode = 0, Cancelled = true, Title = AppIdentity.Name + " restore cancelled",
                    Summary = "Restore cancelled. Completed files remain restored; staged copies were cleaned up.", Elapsed = timer.Elapsed };
            }
        }
    }

    // Returns a result with no title when the user cancels at a prompt (nothing was changed).
    OperationResult PlanRestore(string m8, Snapshot snapshot, List<RestoreEntry> items, RestoreMode mode, string bundle, bool exact,
        Config config, Action beforeRestore)
    {
        var removals = new List<string>();
        var extraFolders = new List<string>();
        if (exact)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry item in items) wanted.Add(item.Relative);
            var scan = new ScanResult();
            CollectFiles(m8, scan);
            foreach (FileInfo file in scan.Files)
                if (!wanted.Contains(file.FullName.Substring(m8.Length))) removals.Add(file.FullName.Substring(m8.Length));
            var wantedFolders = new HashSet<string>(snapshot.Folders, StringComparer.OrdinalIgnoreCase);
            foreach (string folder in scan.Folders)
                if (!wantedFolders.Contains(folder.Substring(m8.Length))) extraFolders.Add(folder.Substring(m8.Length));
        }

        // targetHashes remembers each target file's fingerprint from planning; execution refuses to
        // replace or remove a file that changed since.
        var toCopy = new List<RestoreEntry>();
        var plan = new List<string>();
        var targetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Every restored file, its staging and rollback copies beside it (".gearrollback-<32>.tmp"),
        // and its folders must fit Windows' path limits; check before touching the target.
        var lengths = new PathLengthCheck();
        string root = m8.TrimEnd('\\') + "\\";
        foreach (RestoreEntry item in items)
        {
            // Plain string work: Path methods themselves refuse paths over the limit.
            string target = root + item.Relative;
            lengths.File(target);
            lengths.File(target.Substring(0, target.LastIndexOf('\\')) + "\\.gearrollback-" + new string('0', 32) + ".tmp");
        }
        foreach (string folder in snapshot.Folders)
            if (mode == RestoreMode.All || (bundle != null && (folder.Equals(bundle, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(bundle + "\\", StringComparison.OrdinalIgnoreCase))))
                lengths.Folder(root + folder);
        string pathProblem = lengths.Problem("Restore to a folder with a shorter path, for example D:\\Restore.");
        if (pathProblem != null)
        {
            WriteColored("  " + pathProblem, ConsoleColor.Red);
            return new OperationResult { ExitCode = 1, Summary = pathProblem };
        }
        int differing = 0, identical = 0, newer = 0;
        long bytes = 0, missingBytes = 0;
        foreach (RestoreEntry item in items)
        {
            sizes[item.Relative] = item.Size;
            OperationContext.Current.Progress(Stage.PlanningRestore, item.Relative, 0, 0);
            string target = SafePath(m8, item.Relative);
            CheckParents(m8, target);
            if (Directory.Exists(target)) throw new IOException("A folder blocks the file: " + item.Relative);
            // Preflight every selected backup file, including ones that appear unchanged on the card.
            ValidateEntry(item, config.Verify);
            // Content hashes only when comparing by content; detecting a change since the
            // preview needs only size and modified time.
            string actual = Fingerprint(target, config.Thorough);
            targetHashes[item.Relative] = actual;
            if (EntryMatchesTarget(item, target, config.Thorough, actual)) { identical++; plan.Add("Skip unchanged: " + item.Relative); continue; }
            if (actual != null)
            {
                differing++;
                if (File.GetLastWriteTimeUtc(target).Ticks > item.ModifiedTicks + TimeSpan.FromSeconds(2).Ticks) newer++;
            }
            else missingBytes += item.Size;
            toCopy.Add(item);
            bytes += item.Size;
            plan.Add((actual == null ? "Add: " : "Replace: ") + item.Relative);
        }
        if (newer > 0) WriteColored("  " + newer + " target file(s) are newer than this backup.", ConsoleColor.Yellow);
        if (differing > 0 && !PreviewOnly)
        {
            char answer = Ask("Some target files differ from the backup.", new Option('r', "Replace them"),
                new Option('k', "Keep them (copy only missing files)"), new Option('c', "Cancel"));
            if (answer == 'c') return new OperationResult();
            if (answer == 'k')
            {
                if (exact) { OperationOutput.WriteLine("  Keeping differing files also disables exact removal."); exact = false; removals.Clear(); extraFolders.Clear(); }
                toCopy.RemoveAll(f => targetHashes[f.Relative] != null);
                plan.RemoveAll(line => line.StartsWith("Replace: "));
                bytes = missingBytes;
            }
        }
        foreach (string relative in removals)
        {
            sizes[relative] = new FileInfo(SafePath(m8, relative)).Length;
            targetHashes[relative] = Fingerprint(SafePath(m8, relative), config.Thorough);
            plan.Add("REMOVE extra file: " + relative);
        }
        foreach (string folder in extraFolders) plan.Add("Remove folder if empty: " + folder);
        List<string> folders = snapshot.Folders.FindAll(f => mode == RestoreMode.All || (bundle != null &&
            (f.Equals(bundle, StringComparison.OrdinalIgnoreCase) || f.StartsWith(bundle + "\\", StringComparison.OrdinalIgnoreCase))));
        foreach (string folder in folders)
        {
            string target = SafePath(m8, folder);
            CheckParents(m8, target);
            if (File.Exists(target)) throw new IOException("A file blocks the folder: " + folder);
            if (!Directory.Exists(target)) plan.Add("Create folder: " + folder);
        }
        long targetReserve = RestoreSpaceNeeded(m8, toCopy);
        string error = CheckFreeSpace(m8, targetReserve);
        if (error != null) throw new IOException(error);
        OperationOutput.WriteLine("  Target needs up to {0} working space.", FormatSize(targetReserve));
        if (config.Recovery)
        {
            string recoveryRoot = RecoveryRoot(config);
            RejectRootLinks(recoveryRoot);
            if (Within(recoveryRoot, m8) || Within(m8, recoveryRoot)) throw new IOException("Recovery folder and target must not contain one another.");
            var recoveryPaths = new HashSet<string>(removals, StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry item in toCopy) if (targetHashes[item.Relative] != null) recoveryPaths.Add(item.Relative);
            long recoveryBytes = 0;
            foreach (string relative in recoveryPaths) recoveryBytes += new FileInfo(SafePath(m8, relative)).Length;
            OperationOutput.WriteLine("  Recovery: {0} in {1} ({2} existing file(s)).", FormatSize(recoveryBytes), recoveryRoot, recoveryPaths.Count);
            bool sameVolume = string.Equals(Path.GetPathRoot(recoveryRoot), Path.GetPathRoot(m8), StringComparison.OrdinalIgnoreCase);
            string recoveryError = CheckFreeSpace(recoveryRoot, recoveryBytes + (sameVolume ? targetReserve : 0));
            if (recoveryError != null) throw new IOException(recoveryError);
        }
        else OperationOutput.WriteLine("  Recovery copies: disabled (Recovery=no).");
        OperationOutput.WriteLine("  SHA-256 integrity comparison: " + (config.Verify ? "enabled" : "disabled (Verify=no)"));
        if (!ShowPlan("Restore preview (" + identical + " unchanged)", plan, bytes, config.Preview, sizes)) return new OperationResult();
        if (exact && !AskYesNo("Remove " + removals.Count + " extra file(s) from the selected target?")) return new OperationResult();
        if (beforeRestore != null) beforeRestore();
        OperationResult result = new RestoreEngine().Run(m8, toCopy, removals, folders, extraFolders, targetHashes, config, OperationContext.Current);
        if (result.Counts != null) result.Counts.Unchanged = identical;
        return result;
    }

    OperationResult ExecuteCore(string targetRoot, List<RestoreEntry> copies, List<string> removals, List<string> folders,
        List<string> extraFolders, Dictionary<string, string> targetHashes, Config config)
    {
        // No target mutation until every recovery copy has succeeded.
        string recovery = SaveRecovery(targetRoot, copies, removals, targetHashes, config);
        int copied = 0, failed = 0, removed = 0;
        foreach (string folder in folders)
        {
            OperationContext.Current.CheckCancellation();
            Directory.CreateDirectory(SafePath(targetRoot, folder));
        }
        foreach (RestoreEntry item in copies)
        {
            OperationContext.Current.Progress(Stage.Restoring, item.Relative, 0, 0);
            string target = SafePath(targetRoot, item.Relative);
            string temp = Path.Combine(Path.GetDirectoryName(target), ".gearrestore-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Planning already checked the content; the copy's checksum is compared below.
                ValidateEntry(item, false);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string hash = CopyFile(new FileInfo(item.StoredPath), temp, config.Verify);
                if (config.Verify && item.Hash != null && hash != item.Hash) throw new IOException("Backup checksum changed during restore.");
                DateTime modified = new DateTime(item.ModifiedTicks, DateTimeKind.Utc);
                DateTime created = new DateTime(item.CreatedTicks, DateTimeKind.Utc);
                if (UsableFileTime(modified)) File.SetLastWriteTimeUtc(temp, modified);
                if (UsableFileTime(created)) File.SetCreationTimeUtc(temp, created);
                // Copying can take time: check the current folders again before committing the staged file.
                target = SafePath(targetRoot, item.Relative);
                AssertTargetUnchanged(target, targetHashes[item.Relative]);
                OperationContext.Current.CheckCancellation();
                if (File.Exists(target)) ReplaceSafely(temp, target);
                else File.Move(temp, target);
                copied++;
                OperationOutput.WriteLine("  Restored: " + item.Relative);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; WriteColored("  Failed: " + item.Relative + " (" + ex.Message + ")", ConsoleColor.Red); }
            finally { TryDelete(temp); }
        }
        // Never remove extras after a partially failed restore.
        if (failed == 0)
        {
            foreach (string relative in removals)
            {
                OperationContext.Current.CheckCancellation();
                try
                {
                    string path = SafePath(targetRoot, relative);
                    AssertTargetUnchanged(path, targetHashes[relative]);
                    File.Delete(path);
                    removed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed++; WriteColored("  Removal failed: " + ex.Message, ConsoleColor.Red); break; }
            }
            extraFolders.Sort((a, b) => b.Length.CompareTo(a.Length));
            if (failed == 0) foreach (string relative in extraFolders)
            {
                OperationContext.Current.CheckCancellation();
                string folder = SafePath(targetRoot, relative);
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
            }
        }
        string summary = string.Format("{0} file(s) restored, {1} removed, {2} failed.", copied, removed, failed);
        if (recovery != null) summary += " Recovery: " + recovery;
        OperationOutput.WriteLine("  " + summary);
        return new OperationResult { ExitCode = failed == 0 ? 0 : 2, Summary = summary,
            OutputFolder = targetRoot, Counts = new OperationCounts { Copied = copied, Removed = removed, Failed = failed },
            Title = AppIdentity.Name + (failed == 0 ? " restore complete" : " restore finished with errors") };
    }
}
