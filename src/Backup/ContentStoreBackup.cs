using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static PathSafety;
using static FileCopy;
using static Snapshots;
using static Backups;
using static AtomicFile;
using static BackupStatuses;
using static Histories;
using static PlanReview;
using static DeviceSelection;
using static ConsoleUi;
using static Formatting;
using static SourceScan;
using static BackupFolders;

// One immutable content store per backup set. A manifest commits a complete restore point; the
// current pointer is published last. SQLite is rebuilt from those records and is never needed to restore.
internal sealed class ContentStoreBackup
{
    // Files up to this size are copied by CopyWorkers at a time; larger ones one at a time.
    const long SmallFileLimit = 4L << 20;
    const int CopyWorkers = 4;
    sealed class PlannedFile
    {
        internal FileInfo Source;
        internal string Relative, KnownHash;
        internal bool Unchanged;
        internal long Size, Modified, Created;
    }

    internal OperationResult Run(string source, string history, string previous, bool incremental, Config config)
    {
        DateTime started = DateTime.Now;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string backup = incremental ? previous : Path.Combine(history, started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (!incremental && Directory.Exists(backup)) backup = UniqueFolder(history, started.ToString("yyyy-MM-dd_HHmmss_fffffff", CultureInfo.InvariantCulture));
        OperationOutput.WriteLine("  " + (incremental ? "Updating" : "Creating") + " single content store: " + backup);
        OperationOutput.WriteLine("  A restore point is saved for this backup run. Files are restored through Gear Backup.");
        CatalogData indexed = incremental ? BackupDatabase.TryIncrementalInventory(backup) : null;
        var store = new ObjectChecks(backup);
        Snapshot current = null;
        if (indexed != null)
        {
            current = new Snapshot();
            foreach (var entry in indexed.Files)
            {
                if (entry.IsDirectory) current.Folders.Add(entry.Path);
                else
                {
                    if (entry.Result == "damaged") throw new IOException("Damaged stored content: " + entry.Path + ". Repair this backup before updating it.");
                    current.Files.Add(new RestoreEntry { Relative = entry.Path, Hash = entry.Hash, Size = entry.Size,
                        ModifiedTicks = entry.ModifiedTicks, CreatedTicks = entry.CreatedTicks, StoredPath = store.PathOf(entry.Hash) });
                }
            }
        }
        else if (incremental && File.Exists(Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt"))) current = ContentStore.Current(backup, store);
        var old = new Dictionary<string, RestoreEntry>(StringComparer.OrdinalIgnoreCase);
        if (current != null) foreach (var item in current.Files) old.Add(item.Relative, item);
        var scan = new ScanResult(); CollectFiles(source, scan);
        var files = new List<PlannedFile>(); var actions = new List<string>();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long needed = 0, logicalBytes = 0;
        var length = new PathLengthCheck();
        string nameRoot = Path.GetPathRoot(Path.GetFullPath(backup));
        string pointName = RestorePointNames.Create(backup, started, incremental);
        length.File(Path.Combine(AppIdentity.MetadataRoot(backup), "objects", new string('0', 64)), 5);
        length.File(Path.Combine(SnapshotRoot(backup), pointName + ".manifest"));
        length.File(Path.Combine(AppIdentity.MetadataRoot(backup), ".gearcatalog-" + new string('0', 32) + ".tmp"));
        foreach (var file in scan.Files)
        {
            OperationContext.Current.CheckCancellation();
            string relative = file.FullName.Substring(source.Length);
            CheckedPath(nameRoot, relative); // Validate logical names; no files are created under them, so only the name rules apply.
            names.Add(relative); sizes[relative] = file.Length;
            logicalBytes += file.Length;
            RestoreEntry prior;
            var planned = new PlannedFile { Source = file, Relative = relative, Size = file.Length,
                Modified = file.LastWriteTimeUtc.Ticks, Created = file.CreationTimeUtc.Ticks };
            if (old.TryGetValue(relative, out prior) && prior.Size == file.Length && store.Present(prior.Hash))
            {
                if (new FileInfo(prior.StoredPath).Length != prior.Size) throw new IOException("Damaged stored content: " + relative);
                bool hashCompare = config.Thorough || !UsableFileTime(file.LastWriteTimeUtc) || !UsableFileTime(new DateTime(prior.ModifiedTicks, DateTimeKind.Utc));
                if (hashCompare)
                { planned.KnownHash = HashFile(file.FullName); planned.Unchanged = planned.KnownHash == prior.Hash; }
                else if (Math.Abs(file.LastWriteTimeUtc.Ticks - prior.ModifiedTicks) <= TimeSpan.FromSeconds(2).Ticks)
                { planned.KnownHash = prior.Hash; planned.Unchanged = true; }
            }
            if (!planned.Unchanged)
            { needed += file.Length; actions.Add((old.ContainsKey(relative) ? "Changed: " : "New: ") + relative); }
            files.Add(planned);
        }
        foreach (string relative in old.Keys) if (!names.Contains(relative)) actions.Add("Removed from new restore point: " + relative);
        List<string> nameWarnings = CurrentDevice.NameWarnings ? ChangeSummary.CheckNames(scan, source, config.MaxNameLength) : new List<string>();
        if (nameWarnings.Count > 0)
        {
            WriteColored("  " + nameWarnings.Count + " name(s) the " + CurrentDevice.Name + " may not handle well:", ConsoleColor.Yellow);
            for (int i = 0; i < nameWarnings.Count && i < 10; i++) OperationOutput.WriteLine("    " + nameWarnings[i]);
            if (nameWarnings.Count > 10) OperationOutput.WriteLine("    ... and " + (nameWarnings.Count - 10) + " more");
        }
        string problem = length.Problem("Choose a shorter backup location.");
        if (problem != null) return new OperationResult { ExitCode = 1, Summary = problem };
        // Changes to a source folder's membership also belong to the new point.
        if (current != null)
        {
            var folders = new HashSet<string>(current.Folders, StringComparer.OrdinalIgnoreCase);
            foreach (string folder in scan.Folders)
            { string relative = folder.Substring(source.Length); if (!folders.Remove(relative)) actions.Add("New folder: " + relative); }
            foreach (string folder in folders) actions.Add("Removed folder from new restore point: " + folder);
        }
        foreach (string folder in scan.Folders) CheckedPath(nameRoot, folder.Substring(source.Length));
        string free = CheckFreeSpace(history, needed + (config.Zip && !incremental ? logicalBytes + logicalBytes / 100 + 65536 : 0) + 1024 * 1024);
        if (free != null) return new OperationResult { ExitCode = 1, Summary = free };
        if (PreviewOnly || actions.Count != 0)
            if (!ShowPlan("Single content store backup preview", actions, needed, config.Preview, sizes)) return new OperationResult();
        OperationContext.Current.CheckCancellation();
        Directory.CreateDirectory(backup);
        AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "storage-mode.txt"), "single\n");
        SetBackupStatus(backup, Status.Incomplete);
        BackupDatabase.BeginInventoryUpdate(backup);
        if (!incremental) BackupSetNames.Write(backup, config.FullBackupName);
        AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "device.txt"), CurrentDevice.Id + "\n");
        int copied = 0, unchanged = 0; long added = 0;
        bool committed = false;
        string manifest = null;
        var verifiedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new Snapshot { Name = pointName, TakenUtc = started.ToUniversalTime() };
        foreach (string folder in scan.Folders) snapshot.Folders.Add(folder.Substring(source.Length));
        try
        {
            // Small files are copied by a few workers at once: on a card or a network share their cost is mostly
            // per-file latency. Large files go one at a time afterwards. Results are kept in file order.
            var entries = new RestoreEntry[files.Count];
            var small = new List<int>(); var large = new List<int>();
            for (int i = 0; i < files.Count; i++) (files[i].Size <= SmallFileLimit ? small : large).Add(i);
            var storeLock = new object();
            long addedBytes = 0; int done = 0;
            RestoreEntry Capture(int index)
            {
                PlannedFile item = files[index];
                OperationContext.Current.CheckCancellation();
                OperationContext.Current.Progress(Stage.Copying, item.Relative, Interlocked.Read(ref addedBytes), needed, Interlocked.CompareExchange(ref done, 0, 0), files.Count);
                item.Source.Refresh();
                if (!item.Source.Exists || item.Source.Length != item.Size || item.Source.LastWriteTimeUtc.Ticks != item.Modified)
                    throw new IOException("Source changed since the preview: " + item.Relative);
                RejectRootLinks(item.Source.FullName);
                string hash = item.KnownHash, obj = hash == null ? null : store.PathOf(hash);
                if (obj == null || !File.Exists(obj))
                {
                    string objects = Path.Combine(AppIdentity.MetadataRoot(backup), "objects");
                    string temp = Path.Combine(objects, ".gearobject-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        string copiedHash = CopyFile(item.Source, temp, config.Verify);
                        if (hash != null && copiedHash != hash) throw new IOException("Source checksum changed since the preview: " + item.Relative);
                        hash = copiedHash; obj = store.PathOf(hash);
                        // Two workers can hold the same new content; the first to publish it wins.
                        lock (storeLock)
                        {
                            if (File.Exists(obj))
                            {
                                if (new FileInfo(obj).Length != item.Size || (config.Verify && verifiedObjects.Add(hash) && HashFileFromDisk(obj) != hash))
                                    throw new IOException("Damaged stored content: " + item.Relative);
                            }
                            else { File.Move(temp, obj); Interlocked.Add(ref addedBytes, item.Size); if (config.Verify) verifiedObjects.Add(hash); }
                        }
                    }
                    finally { TryDelete(temp); }
                }
                else
                {
                    bool damaged;
                    lock (storeLock) damaged = new FileInfo(obj).Length != item.Size || (config.Verify && verifiedObjects.Add(hash) && HashFileFromDisk(obj) != hash);
                    if (damaged) throw new IOException("Damaged stored content: " + item.Relative);
                }
                Interlocked.Increment(ref done);
                return new RestoreEntry { Relative = item.Relative, StoredPath = obj, Hash = hash, Size = item.Size,
                    ModifiedTicks = item.Modified, CreatedTicks = item.Created };
            }
            string objectsFolder = Path.Combine(AppIdentity.MetadataRoot(backup), "objects");
            RejectRootLinks(objectsFolder); Directory.CreateDirectory(objectsFolder);
            if (small.Count > 1)
            {
                OperationContext context = OperationContext.Current;
                try
                {
                    Parallel.ForEach(small, new ParallelOptions { MaxDegreeOfParallelism = CopyWorkers, CancellationToken = context.Cancellation }, index =>
                    {
                        using (context.Activate()) entries[index] = Capture(index);
                    });
                }
                catch (AggregateException ex)
                {
                    // Report the real failure, not the cancellations it caused in the other workers.
                    Exception first = null;
                    foreach (Exception inner in ex.Flatten().InnerExceptions)
                        if (first == null || (first is OperationCanceledException && !(inner is OperationCanceledException))) first = inner;
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
                }
            }
            else foreach (int index in small) entries[index] = Capture(index);
            foreach (int index in large) entries[index] = Capture(index);
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].Unchanged) unchanged++; else copied++;
                snapshot.Files.Add(entries[i]);
            }
            added = addedBytes;
            // Nothing names a new restore point until every referenced object has been captured.
            OperationContext.Current.CheckCancellation();
            var rows = new StringBuilder(AppIdentity.SnapshotHeader + "\n");
            foreach (string folder in snapshot.Folders) rows.Append("D\t").Append(Encode(folder)).Append('\n');
            foreach (var item in snapshot.Files)
                rows.Append("F\t").Append(Encode(item.Relative)).Append('\t').Append(item.Hash).Append('\t')
                    .Append(item.Size.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(item.ModifiedTicks.ToString(CultureInfo.InvariantCulture))
                    .Append('\t').Append(item.CreatedTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            manifest = SafePath(SnapshotRoot(backup), snapshot.Name + ".manifest");
            AtomicText(manifest, rows.ToString());
            RestorePointNames.SaveMetadata(backup, snapshot.Name, started, incremental, incremental ? config.IncrementalBackupName : null);
            AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt"), snapshot.Name + "\n");
            committed = true;
            SetBackupStatus(backup, Status.Complete);
            AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "last-success.txt"), DateTime.UtcNow.ToString("O") + "\n");
            BackupDatabase.TryRefresh(backup);
            string zipPath = null; bool zipFailed = false;
            if (config.Zip && incremental) OperationOutput.WriteLine("  --zip applies to full backups; no zip made for this incremental backup.");
            else if (config.Zip)
            {
                try { OperationOutput.WriteLine("  Saving zip..."); zipPath = BackupZip.ZipBackup(backup); OperationOutput.WriteLine("  Zip saved to " + zipPath); }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException) throw;
                    zipFailed = true; OperationOutput.WriteLine("  Could not save the zip: " + ex.Message);
                }
            }
            if (config.KeepBackups > 0) PruneBackups(history, config.KeepBackups, backup);
            string summary = "Restore point saved: " + (incremental ? snapshot.Name : started.ToString("yyyy-MM-dd HH:mm:ss")) + ". " + copied + " new/changed, " + unchanged + " unchanged; " + FormatSize(added) + " added to the content store." +
                (zipPath != null ? " Zip: " + Path.GetFileName(zipPath) + "." : "") + (zipFailed ? " The zip could not be saved." : "");
            OperationOutput.WriteLine("  " + summary);
            return new OperationResult { Title = AppIdentity.Name + " complete", Summary = summary, ExitCode = zipFailed ? 2 : 0, OutputFolder = backup,
                Elapsed = timer.Elapsed, Counts = new OperationCounts { Copied = copied, Unchanged = unchanged } };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!committed) SetBackupStatus(backup, Status.Failed);
            OperationOutput.WriteLine("  " + (committed ? "Follow-up failed: " : "Backup failed: ") + ex.Message);
            return new OperationResult { Title = AppIdentity.Name + (committed ? " follow-up failed" : " backup failed"),
                Summary = ex.Message + (committed ? " The restore point was saved." : " Previous restore points are preserved."), ExitCode = 2,
                OutputFolder = backup, Elapsed = timer.Elapsed, Counts = new OperationCounts { Copied = copied, Unchanged = unchanged, Failed = 1 } };
        }
        finally
        {
            if (!committed && manifest != null)
            {
                try
                {
                    foreach (string path in new[] { manifest, Path.ChangeExtension(manifest, ".kind"), Path.ChangeExtension(manifest, ".taken"), Path.ChangeExtension(manifest, ".name") })
                    { RejectRootLinks(path); TryDelete(path); }
                }
                catch (Exception ex) { OperationOutput.WriteLine("  Could not remove unpublished restore point: " + ex.Message); }
            }
        }
    }
}
