using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static GearBackup.PathSafety;
using static GearBackup.FileCopy;
using static GearBackup.Snapshots;
using static GearBackup.Backups;
using static GearBackup.AtomicFile;
using static GearBackup.BackupStatuses;
using static GearBackup.Histories;
using static GearBackup.PlanReview;
using static GearBackup.DeviceSelection;
using static GearBackup.ConsoleUi;
using static GearBackup.Formatting;
using static GearBackup.SourceScan;
using static GearBackup.BackupFolders;

namespace GearBackup
{
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

        // One run's state, shared by the phases below (a ContentStoreBackup is made for each run).
    readonly OperationContext context;
        string source, history, backup, pointName, manifest;
        bool incremental;
        Config config;
        DateTime started;
        System.Diagnostics.Stopwatch timer;
        ObjectChecks store;
        Snapshot current, snapshot;
        ScanResult scan;
        readonly List<PlannedFile> files = new List<PlannedFile>();
        readonly List<string> actions = new List<string>();
        readonly Dictionary<string, long> sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long needed, logicalBytes;
        readonly object storeLock = new object();
        readonly HashSet<string> verifiedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long addedBytes;
        int doneFiles, copied, unchanged;
        bool committed;

        internal ContentStoreBackup(OperationContext context) { this.context = context; }

    internal OperationResult Run(string source, string history, string previous, bool incremental, Config config)
        {
            this.source = source; this.history = history; this.incremental = incremental; this.config = config;
            started = DateTime.Now;
            timer = System.Diagnostics.Stopwatch.StartNew();
            backup = incremental ? previous : Path.Combine(history, started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!incremental && Directory.Exists(backup)) backup = UniqueFolder(history, started.ToString("yyyy-MM-dd_HHmmss_fffffff", CultureInfo.InvariantCulture));
            OperationOutput.WriteLine("  " + (incremental ? "Updating" : "Creating") + " single content store: " + backup);
            OperationOutput.WriteLine("  A restore point is saved for this backup run. Files are restored through Gear Backup.");
            store = new ObjectChecks(backup);
            StartAfterLatestPoint();
            LoadCurrentPoint();
            OperationResult stop = PlanFiles();
            if (stop != null) return stop;
            PrepareBackupFolder();
            try
            {
                CaptureFiles();
                CommitRestorePoint();
                string zipPath; bool zipFailed;
                ExportZip(out zipPath, out zipFailed);
                if (config.KeepBackups > 0) PruneBackups(history, config.KeepBackups, backup);
                return Finished(zipPath, zipFailed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!committed) SetBackupStatus(backup, Status.Failed);
                OperationOutput.WriteLine("  " + (committed ? "Follow-up failed: " : "Backup failed: ") + ex.Message);
                string hint = ErrorHints.For(ex);
                if (hint != null) OperationOutput.WriteLine("  " + hint);
                return new OperationResult { Title = AppIdentity.Name + (committed ? " follow-up failed" : " backup failed"),
                    Summary = ex.Message + (committed ? " The restore point was saved." : " Previous restore points are preserved."), ExitCode = 2,
                    OutputFolder = backup, Elapsed = timer.Elapsed, Counts = new OperationCounts { Copied = copied, Unchanged = unchanged, Failed = 1 } };
            }
            finally { RemoveUnpublishedPoint(); }
        }

        // Restore points are ordered by capture time. A run that starts in the same clock tick as the previous one
        // (a fast run on a small set) would otherwise tie with it and could be listed as the older point.
        void StartAfterLatestPoint()
        {
            if (!incremental) return;
            string pointer = Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt");
            if (!File.Exists(pointer)) return;
            try
            {
                string latest = Records.ReadText(pointer, Records.SmallLimit, "Current restore point").Trim();
                DateTime taken = RestorePointNames.TakenUtc(backup, latest);
                if (started.ToUniversalTime() <= taken) started = taken.ToLocalTime().AddMilliseconds(1);
            }
            catch (IOException) { } // a damaged record is reported by the run itself
        }

        // The restore point an incremental run builds on: from the SQLite inventory when it is current, otherwise the manifest.
        void LoadCurrentPoint()
        {
            CatalogData indexed = incremental ? BackupDatabase.TryIncrementalInventory(backup) : null;
            if (indexed != null)
            {
                current = new Snapshot();
                foreach (var entry in indexed.Files)
                {
                    if (entry.IsDirectory) current.Folders.Add(entry.Path);
                    else
                    {
                        if (entry.Result == "damaged") throw new DamagedContentException("Damaged stored content: " + entry.Path + ". Repair this backup before updating it.");
                        current.Files.Add(new RestoreEntry { Relative = entry.Path, Hash = entry.Hash, Size = entry.Size,
                            ModifiedTicks = entry.ModifiedTicks, CreatedTicks = entry.CreatedTicks, StoredPath = store.PathOf(entry.Hash) });
                    }
                }
            }
            else if (incremental && File.Exists(Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt"))) current = ContentStore.Current(backup, store);
        }

        // Scans the source and decides what is new, changed, unchanged or gone. Returns a result when the run must stop
        // here (a path too long, too little space, a declined plan), null to go on.
        OperationResult PlanFiles()
        {
            var old = new Dictionary<string, RestoreEntry>(StringComparer.OrdinalIgnoreCase);
            if (current != null) foreach (var item in current.Files) old.Add(item.Relative, item);
            scan = new ScanResult(); CollectFiles(source, scan);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var length = new PathLengthCheck();
            string nameRoot = Path.GetPathRoot(Path.GetFullPath(backup));
            pointName = RestorePointNames.Create(backup, started, incremental);
            length.File(Path.Combine(AppIdentity.MetadataRoot(backup), "objects", new string('0', 64)), 5);
            length.File(Path.Combine(SnapshotRoot(backup), pointName + ".manifest"));
            length.File(Path.Combine(AppIdentity.MetadataRoot(backup), ".gearcatalog-" + new string('0', 32) + ".tmp"));
            foreach (var file in scan.Files)
            {
                context.CheckCancellation();
                string relative = file.FullName.Substring(source.Length);
                CheckedPath(nameRoot, relative); // Validate logical names; no files are created under them, so only the name rules apply.
                names.Add(relative); sizes[relative] = file.Length;
                logicalBytes += file.Length;
                PlannedFile planned = Compare(file, relative, old);
                if (!planned.Unchanged)
                { needed += file.Length; actions.Add((old.ContainsKey(relative) ? "Changed: " : "New: ") + relative); }
                files.Add(planned);
            }
            foreach (string relative in old.Keys) if (!names.Contains(relative)) actions.Add("Removed from new restore point: " + relative);
            WarnAboutNames();
            string problem = length.Problem("Choose a shorter backup location.");
            if (problem != null) return new OperationResult { ExitCode = 1, Summary = problem };
            PlanFolders(nameRoot);
            string free = CheckFreeSpace(history, needed + (config.Zip && !incremental ? logicalBytes + logicalBytes / 100 + 65536 : 0) + 1024 * 1024);
            if (free != null) return new OperationResult { ExitCode = 1, Summary = free };
            if (PreviewOnly || actions.Count != 0)
                if (!ShowPlan("Single content store backup preview", actions, needed, config.Preview, sizes)) return new OperationResult();
            return null;
        }

        // Whether a source file equals the stored one: by size and time, or by checksum when asked or when its time can't be trusted.
        PlannedFile Compare(FileInfo file, string relative, Dictionary<string, RestoreEntry> old)
        {
            RestoreEntry prior;
            var planned = new PlannedFile { Source = file, Relative = relative, Size = file.Length,
                Modified = file.LastWriteTimeUtc.Ticks, Created = file.CreationTimeUtc.Ticks };
            if (old.TryGetValue(relative, out prior) && prior.Size == file.Length && store.Present(prior.Hash))
            {
                if (new FileInfo(prior.StoredPath).Length != prior.Size) throw new DamagedContentException("Damaged stored content: " + relative);
                bool hashCompare = config.Thorough || !UsableFileTime(file.LastWriteTimeUtc) || !UsableFileTime(new DateTime(prior.ModifiedTicks, DateTimeKind.Utc));
                if (hashCompare)
                { planned.KnownHash = HashFile(file.FullName); planned.Unchanged = planned.KnownHash == prior.Hash; }
                else if (Math.Abs(file.LastWriteTimeUtc.Ticks - prior.ModifiedTicks) <= TimeSpan.FromSeconds(2).Ticks)
                { planned.KnownHash = prior.Hash; planned.Unchanged = true; }
            }
            return planned;
        }

        void WarnAboutNames()
        {
            List<string> nameWarnings = CurrentDevice.NameWarnings ? ChangeSummary.CheckNames(scan, source, config.MaxNameLength) : new List<string>();
            if (nameWarnings.Count == 0) return;
            WriteColored("  " + nameWarnings.Count + " name(s) the " + CurrentDevice.Name + " may not handle well:", ConsoleColor.Yellow);
            for (int i = 0; i < nameWarnings.Count && i < 10; i++) OperationOutput.WriteLine("    " + nameWarnings[i]);
            if (nameWarnings.Count > 10) OperationOutput.WriteLine("    ... and " + (nameWarnings.Count - 10) + " more");
        }

        // Changes to a source folder's membership also belong to the new point.
        void PlanFolders(string nameRoot)
        {
            if (current != null)
            {
                var folders = new HashSet<string>(current.Folders, StringComparer.OrdinalIgnoreCase);
                foreach (string folder in scan.Folders)
                { string relative = folder.Substring(source.Length); if (!folders.Remove(relative)) actions.Add("New folder: " + relative); }
                foreach (string folder in folders) actions.Add("Removed folder from new restore point: " + folder);
            }
            foreach (string folder in scan.Folders) CheckedPath(nameRoot, folder.Substring(source.Length));
        }

        // From here on the backup folder exists and is marked unfinished until the restore point is committed.
        void PrepareBackupFolder()
        {
            context.CheckCancellation();
            Directory.CreateDirectory(backup);
            AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "storage-mode.txt"), "single\n");
            SetBackupStatus(backup, Status.Incomplete);
            BackupDatabase.BeginInventoryUpdate(backup);
            if (!incremental) BackupSetNames.Write(backup, config.FullBackupName);
            AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "device.txt"), CurrentDevice.Id + "\n");
            snapshot = new Snapshot { Name = pointName, TakenUtc = started.ToUniversalTime() };
            foreach (string folder in scan.Folders) snapshot.Folders.Add(folder.Substring(source.Length));
        }

        // Small files are copied by a few workers at once: on a card or a network share their cost is mostly
        // per-file latency. Large files go one at a time afterwards. Results are kept in file order.
        void CaptureFiles()
        {
            var entries = new RestoreEntry[files.Count];
            var small = new List<int>(); var large = new List<int>();
            for (int i = 0; i < files.Count; i++) (files[i].Size <= SmallFileLimit ? small : large).Add(i);
            string objectsFolder = Path.Combine(AppIdentity.MetadataRoot(backup), "objects");
            RejectRootLinks(objectsFolder); Directory.CreateDirectory(objectsFolder);
            if (small.Count > 1)
            {
                try
                {
                    Parallel.ForEach(small, new ParallelOptions { MaxDegreeOfParallelism = CopyWorkers, CancellationToken = context.Cancellation }, index =>
                    {
                        using (context.Activate()) entries[index] = CaptureFile(index);
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
            else foreach (int index in small) entries[index] = CaptureFile(index);
            foreach (int index in large) entries[index] = CaptureFile(index);
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].Unchanged) unchanged++; else copied++;
                snapshot.Files.Add(entries[i]);
            }
        }

        // Stores one source file's content (unless it is already stored) and returns its entry for the restore point.
        RestoreEntry CaptureFile(int index)
        {
            PlannedFile item = files[index];
            context.CheckCancellation();
            context.Progress(Stage.Copying, item.Relative, Interlocked.Read(ref addedBytes), needed, Interlocked.CompareExchange(ref doneFiles, 0, 0), files.Count);
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
                                throw new DamagedContentException("Damaged stored content: " + item.Relative);
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
                if (damaged) throw new DamagedContentException("Damaged stored content: " + item.Relative);
            }
            Interlocked.Increment(ref doneFiles);
            return new RestoreEntry { Relative = item.Relative, StoredPath = obj, Hash = hash, Size = item.Size,
                ModifiedTicks = item.Modified, CreatedTicks = item.Created };
        }

        // Nothing names a new restore point until every referenced object has been captured; the pointer to it is published last.
        void CommitRestorePoint()
        {
            context.CheckCancellation();
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
        }

        // --zip: after a full backup, also saves it as a zip next to its folder. A zip that fails leaves the backup complete.
        void ExportZip(out string zipPath, out bool zipFailed)
        {
            zipPath = null; zipFailed = false;
            if (!config.Zip) return;
            if (incremental) { OperationOutput.WriteLine("  --zip applies to full backups; no zip made for this incremental backup."); return; }
            try { OperationOutput.WriteLine("  Saving zip..."); zipPath = BackupZip.ZipBackup(backup); OperationOutput.WriteLine("  Zip saved to " + zipPath); }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                zipFailed = true; OperationOutput.WriteLine("  Could not save the zip: " + ex.Message);
            }
        }

        OperationResult Finished(string zipPath, bool zipFailed)
        {
            string summary = "Restore point saved: " + (incremental ? snapshot.Name : started.ToString("yyyy-MM-dd HH:mm:ss")) + ". " + copied + " new/changed, " + unchanged + " unchanged; " + FormatSize(addedBytes) + " added to the content store." +
                (zipPath != null ? " Zip: " + Path.GetFileName(zipPath) + "." : "") + (zipFailed ? " The zip could not be saved." : "");
            OperationOutput.WriteLine("  " + summary);
            return new OperationResult { Title = AppIdentity.Name + " complete", Summary = summary, ExitCode = zipFailed ? 2 : 0, OutputFolder = backup,
                Elapsed = timer.Elapsed, Counts = new OperationCounts { Copied = copied, Unchanged = unchanged } };
        }

        // A manifest written for a run that then failed must not stay behind as a restore point.
        void RemoveUnpublishedPoint()
        {
            if (committed || manifest == null) return;
            try
            {
                foreach (string path in new[] { manifest, Path.ChangeExtension(manifest, ".kind"), Path.ChangeExtension(manifest, ".taken"), Path.ChangeExtension(manifest, ".name") })
                { RejectRootLinks(path); TryDelete(path); }
            }
            catch (Exception ex) { OperationOutput.WriteLine("  Could not remove unpublished restore point: " + ex.Message); }
        }
    }

}
