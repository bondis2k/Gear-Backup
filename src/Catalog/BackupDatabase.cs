using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using static PathSafety;
using static Snapshots;
using static AtomicFile;
using static BackupStatuses;
using static PlanReview;

// Primary incremental inventory, recoverable from checksum lists and manifests.
// Build a new local database, close it, then publish a single file; never edit a database on a share.
internal static class BackupDatabase
{
    internal const string FileName = "catalog.sqlite";
    const int ApplicationId = 1195524674, SchemaVersion = 1, RowLimit = 1000000;
    internal static string CatalogPath(string backup) { return Path.Combine(AppIdentity.MetadataRoot(backup), FileName); }

    internal static void TryRefresh(string backup, List<CatalogVerification> verified = null, string checkSummary = null)
    {
        if (PreviewOnly) return;
        try { Rebuild(backup, verified, checkSummary); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { OperationOutput.WriteLine("  Could not update the backup catalog (backup files remain available): " + ex.Message); }
    }

    internal static void Rebuild(string backup, List<CatalogVerification> verified = null, string checkSummary = null)
    {
        OperationContext.Current.CheckCancellation();
        RejectRootLinks(backup);
        if (!Directory.Exists(backup)) throw new IOException("Backup folder does not exist: " + backup);
        string path = CatalogPath(backup);
        RejectRootLinks(path);
        var previous = new CatalogData();
        if (File.Exists(path))
        {
            // A rebuild only carries forward recorded checks and the check history, so it reads just those rows.
            try { previous = Read(backup, false, null, true); }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) { OperationOutput.WriteLine("  Rebuilding the unreadable catalog from checksum lists and manifests: " + ex.Message); }
        }
        CatalogData data = Inventory(backup);
        var old = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previous.Files) old[Key(entry.SnapshotId, entry.Path)] = entry;
        var checks = new Dictionary<string, CatalogVerification>(StringComparer.OrdinalIgnoreCase);
        if (verified != null) foreach (var check in verified) checks[Key(check.SnapshotId, check.Path)] = check;
        foreach (var entry in data.Files)
        {
            CatalogEntry prior;
            // A recorded check applies only to the same expected version and observed metadata.
            if (old.TryGetValue(Key(entry.SnapshotId, entry.Path), out prior) && entry.Hash == prior.Hash &&
                entry.Size == prior.Size && entry.ModifiedTicks == prior.ModifiedTicks && entry.Present == prior.Present)
            { entry.VerifiedUtc = prior.VerifiedUtc; entry.Result = prior.Result; entry.ObservedHash = prior.ObservedHash; }
            CatalogVerification check;
            if (checks.TryGetValue(Key(entry.SnapshotId, entry.Path), out check) && check.ExpectedHash == entry.Hash)
            { entry.VerifiedUtc = check.CheckedUtc; entry.Result = check.Result; entry.ObservedHash = check.ObservedHash; }
        }
        data.Checks.AddRange(previous.Checks);
        if (checkSummary != null) data.Checks.Add(new CatalogCheckRun { CheckedUtc = DateTime.UtcNow.ToString("O"), Summary = checkSummary });
        if (data.Checks.Count > 100) data.Checks.RemoveRange(0, data.Checks.Count - 100);
        Publish(backup, path, data);
    }

    static string Key(string snapshot, string path) { return snapshot + "\0" + path; }
    static CatalogData Inventory(string backup)
    {
        ContentStoreBackup.RequireContentStore(backup);
        var data = new CatalogData();
        var objects = new ObjectChecks(backup);
        Snapshot current = ContentStoreBackup.Current(backup, objects);
        foreach (string folder in current.Folders) Add(data, new CatalogEntry { Path = folder, IsDirectory = true, Present = true, Size = 0 });
        foreach (var item in current.Files) Add(data, new CatalogEntry { Path = item.Relative, Hash = item.Hash, Size = item.Size,
            ModifiedTicks = item.ModifiedTicks, CreatedTicks = item.CreatedTicks, Present = objects.Present(item.Hash) });
        string snapshots = SnapshotRoot(backup);
        RejectRootLinks(snapshots);
        var validNames = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(snapshots)) foreach (string manifest in Directory.GetFiles(snapshots, "*.manifest"))
        {
            OperationContext.Current.CheckCancellation();
            Snapshot snapshot = ReadSnapshot(backup, manifest, objects, validNames);
            data.RestorePoints.Add(snapshot.Name);
            data.PointKinds[snapshot.Name] = ReadPointKind(backup, snapshot.Name);
            data.PointTimes[snapshot.Name] = snapshot.TakenUtc;
            if (snapshot.CustomName != null) data.PointNames[snapshot.Name] = snapshot.CustomName;
            foreach (string folder in snapshot.Folders) Add(data, new CatalogEntry { SnapshotId = snapshot.Name, Path = folder, IsDirectory = true, Size = 0, Present = true });
            foreach (var item in snapshot.Files)
            {
                Add(data, new CatalogEntry { SnapshotId = snapshot.Name, Path = item.Relative, Hash = item.Hash,
                    Size = item.Size, ModifiedTicks = item.ModifiedTicks, CreatedTicks = item.CreatedTicks, Present = objects.Present(item.Hash) });
            }
        }
        return data;
    }
    static void Add(CatalogData data, CatalogEntry entry)
    {
        if (data.Files.Count >= RowLimit) throw new IOException("Backup catalog has too many entries.");
        data.Files.Add(entry);
    }

    static void Publish(string backup, string path, CatalogData data)
    {
        string local = Path.Combine(Path.GetTempPath(), "gear-catalog-" + Guid.NewGuid().ToString("N") + ".sqlite");
        // Reserve the local filename before letting SQLite open it.
        using (new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        string staged = Path.Combine(Path.GetDirectoryName(path), ".gearcatalog-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool stagedCreated = false;
        try
        {
            using (var db = new SqliteDatabase(local, false))
            {
                // This is a staging file: it is copied to the backup and flushed to disk below, and a half-written one is
                // simply discarded, so it needs no journal or sync of its own. One transaction covers the schema and the rows,
                // instead of a commit (and disk sync) per statement.
                db.Execute("PRAGMA journal_mode=OFF");
                db.Execute("PRAGMA synchronous=OFF");
                db.Execute("BEGIN IMMEDIATE");
                db.Execute("PRAGMA application_id=" + ApplicationId);
                db.Execute("PRAGMA user_version=" + SchemaVersion);
                db.Execute("CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
                db.Execute("CREATE TABLE restore_points (name TEXT PRIMARY KEY)");
                db.Execute("CREATE TABLE files (snapshot_id TEXT NOT NULL, path TEXT NOT NULL, is_directory INTEGER NOT NULL, size INTEGER, modified_ticks INTEGER, created_ticks INTEGER, expected_sha256 TEXT, present INTEGER NOT NULL, last_verified_utc TEXT, verification_result TEXT NOT NULL, observed_sha256 TEXT, PRIMARY KEY(snapshot_id,path))");
                db.Execute("CREATE INDEX files_integrity ON files(verification_result)");
                db.Execute("CREATE INDEX files_path ON files(path)");
                db.Execute("CREATE TABLE check_runs (id INTEGER PRIMARY KEY, checked_utc TEXT NOT NULL, summary TEXT NOT NULL)");
                db.Execute("INSERT INTO metadata VALUES (?,?)", "built_utc", DateTime.UtcNow.ToString("O"));
                db.Execute("INSERT INTO metadata VALUES (?,?)", "app_version", AppIdentity.Version);
                db.Execute("INSERT INTO metadata VALUES (?,?)", "backup_name", BackupSetNames.Read(backup));
                db.Execute("INSERT INTO metadata VALUES (?,?)", "device_id", (DeviceSelection.BackupDevice(backup) ?? DeviceSelection.CurrentDevice).Id);
                db.Execute("INSERT INTO metadata VALUES (?,?)", "inventory_stamp", InventoryStamp(backup));
                db.Execute("INSERT INTO metadata VALUES (?,?)", "inventory_generation", InventoryGeneration(backup));
                using (var insert = db.Prepare("INSERT INTO files VALUES (?,?,?,?,?,?,?,?,?,?,?)"))
                    foreach (var entry in data.Files)
                    {
                        OperationContext.Current.CheckCancellation();
                        insert.Bind(entry.SnapshotId, entry.Path, entry.IsDirectory ? 1 : 0, entry.Size < 0 ? (object)null : entry.Size,
                            entry.ModifiedTicks == 0 ? (object)null : entry.ModifiedTicks, entry.CreatedTicks == 0 ? (object)null : entry.CreatedTicks,
                            entry.Hash, entry.Present ? 1 : 0, entry.VerifiedUtc, entry.Result, entry.ObservedHash);
                        insert.Read();
                    }
                foreach (var check in data.Checks) db.Execute("INSERT INTO check_runs(checked_utc,summary) VALUES (?,?)", check.CheckedUtc, check.Summary);
                foreach (string point in data.RestorePoints) db.Execute("INSERT INTO restore_points VALUES (?)", point);
                foreach (var kind in data.PointKinds) db.Execute("INSERT INTO metadata VALUES (?,?)", "point_kind:" + kind.Key, kind.Value);
                foreach (var time in data.PointTimes) db.Execute("INSERT INTO metadata VALUES (?,?)", "point_taken:" + time.Key, time.Value.ToString("O", CultureInfo.InvariantCulture));
                foreach (var named in data.PointNames) db.Execute("INSERT INTO metadata VALUES (?,?)", "point_name:" + named.Key, named.Value);
                db.Execute("COMMIT");
            }
            if (new FileInfo(local).Length > Records.ListLimit) throw new IOException("Backup catalog is too large.");
            OperationContext.Current.CheckCancellation();
            RejectRootLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var input = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stagedCreated = true;
                var buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                { OperationContext.Current.CheckCancellation(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            OperationContext.Current.CheckCancellation();
            RejectRootLinks(path);
            if (File.Exists(path)) ReplaceSafely(staged, path); else File.Move(staged, path);
        }
        finally
        {
            if (stagedCreated) FileCopy.TryDelete(staged);
            FileCopy.TryDelete(local); FileCopy.TryDelete(local + "-journal");
        }
    }

    // currentOnly: just the current listing. verifiedOnly: just the rows that carry a recorded check, and the check runs, not
    // the restore point list; what Rebuild needs from the old catalog (every other row is rebuilt from the manifests).
    internal static CatalogData Read(string backup, bool currentOnly = false, InventoryPathCache inventoryPaths = null, bool verifiedOnly = false)
    {
        inventoryPaths = inventoryPaths ?? new InventoryPathCache(backup);
        string path = CatalogPath(backup);
        // Copy bytes through the bounded record reader. SQLite never opens an untrusted share or link.
        byte[] contents = Records.ReadBytes(path, Records.ListLimit, "Backup catalog");
        string local = Path.Combine(Path.GetTempPath(), "gear-catalog-read-" + Guid.NewGuid().ToString("N") + ".sqlite");
        bool created = false;
        try
        {
            using (var file = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; file.Write(contents, 0, contents.Length); }
            using (var db = new SqliteDatabase(local, true))
            {
                using (var version = db.Prepare("PRAGMA user_version"))
                    if (!version.Read() || version.Number(0) != SchemaVersion) throw new IOException("Unsupported backup catalog schema.");
                using (var id = db.Prepare("PRAGMA application_id"))
                    if (!id.Read() || id.Number(0) != ApplicationId) throw new IOException("Not a Gear Backup catalog.");
                var result = new CatalogData();
                using (var device = db.Prepare("SELECT value FROM metadata WHERE key='device_id'"))
                    if (device.Read()) result.DeviceId = device.String(0);
                using (var stamp = db.Prepare("SELECT value FROM metadata WHERE key='inventory_stamp'"))
                    if (stamp.Read()) result.InventoryStamp = stamp.String(0);
                using (var generation = db.Prepare("SELECT value FROM metadata WHERE key='inventory_generation'"))
                    if (generation.Read()) result.InventoryGeneration = generation.String(0);
                using (var files = db.Prepare("SELECT snapshot_id,path,is_directory,size,modified_ticks,created_ticks,expected_sha256,present,last_verified_utc,verification_result,observed_sha256 FROM files" + (currentOnly ? " WHERE snapshot_id='current'" :
                    verifiedOnly ? " WHERE verification_result IS NOT 'unchecked' OR last_verified_utc IS NOT NULL OR observed_sha256 IS NOT NULL" : "")))
                    while (files.Read())
                    {
                        string relative = files.String(1), hash = files.String(6), observed = files.String(10), status = files.String(9);
                        inventoryPaths.Resolve(relative);
                        if ((hash != null && !ValidHash(hash)) || (observed != null && !ValidHash(observed)) ||
                            (status != "unchecked" && status != "ok" && status != "damaged" && status != "missing" && status != "unreadable"))
                            throw new IOException("Invalid backup catalog integrity record.");
                        string snapshot = files.String(0), time = files.String(8);
                        if (string.IsNullOrEmpty(snapshot) || snapshot.Length > 128 || (time != null && !ValidTime(time))) throw new IOException("Invalid backup catalog record.");
                        Add(result, new CatalogEntry { SnapshotId = snapshot, Path = relative, IsDirectory = files.Number(2) != 0,
                            Size = files.String(3) == null ? -1 : files.Number(3), ModifiedTicks = files.Number(4), CreatedTicks = files.Number(5),
                            Hash = hash, Present = files.Number(7) != 0, VerifiedUtc = time, Result = status, ObservedHash = observed });
                    }
                if (!currentOnly && !verifiedOnly)
                {
                    using (var times = db.Prepare("SELECT key,value FROM metadata WHERE key LIKE 'point_taken:%'"))
                        while (times.Read())
                        {
                            if (result.PointTimes.Count >= RowLimit) throw new IOException("Too many restore point capture times.");
                            string name = times.String(0).Substring(12); DateTime time;
                            SafePath(SnapshotRoot(backup), name + ".taken");
                            if (!DateTime.TryParseExact(times.String(1), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time) || time.Kind != DateTimeKind.Utc)
                                throw new IOException("Invalid restore point capture time.");
                            result.PointTimes.Add(name, time);
                        }
                    using (var names = db.Prepare("SELECT key,value FROM metadata WHERE key LIKE 'point_name:%'"))
                        while (names.Read())
                        {
                            if (result.PointNames.Count >= RowLimit) throw new IOException("Too many restore point names.");
                            string name = names.String(0).Substring(11), custom = names.String(1);
                            if (custom == null || custom.Length == 0 || custom.Length > 80) throw new IOException("Invalid restore point name.");
                            result.PointNames[name] = custom;
                        }
                    using (var kinds = db.Prepare("SELECT key,value FROM metadata WHERE key LIKE 'point_kind:%'"))
                        while (kinds.Read())
                        {
                            if (result.PointKinds.Count >= RowLimit) throw new IOException("Too many restore point kinds.");
                            string name = kinds.String(0).Substring(11), kind = kinds.String(1);
                            SafePath(SnapshotRoot(backup), name + ".kind");
                            if (kind != "full" && kind != "incremental") throw new IOException("Invalid restore point kind.");
                            result.PointKinds[name] = kind;
                        }
                    using (var points = db.Prepare("SELECT name FROM restore_points ORDER BY name"))
                            while (points.Read())
                            {
                                if (result.RestorePoints.Count >= RowLimit) throw new IOException("Too many restore points.");
                                string name = points.String(0);
                                SafePath(SnapshotRoot(backup), name + ".manifest");
                                if (string.IsNullOrEmpty(name) || name.Length > 128) throw new IOException("Invalid restore point name.");
                                if (!result.PointKinds.ContainsKey(name)) throw new IOException("Missing restore point kind.");
                                if (!result.PointTimes.ContainsKey(name)) throw new IOException("Missing restore point capture time.");
                                result.RestorePoints.Add(name);
                            }
                }
                if (!currentOnly) using (var checks = db.Prepare("SELECT checked_utc,summary FROM check_runs ORDER BY id DESC LIMIT 100"))
                    while (checks.Read())
                    {
                        string time = checks.String(0), summary = checks.String(1);
                        if (!ValidTime(time) || summary == null || summary.Length > 4096) throw new IOException("Invalid backup catalog check run.");
                        result.Checks.Add(new CatalogCheckRun { CheckedUtc = time, Summary = summary });
                    }
                result.Checks.Reverse(); return result;
            }
        }
        finally { if (created) FileCopy.TryDelete(local); }
    }
    static bool ValidTime(string text)
    { DateTime value; return DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value); }
    internal static string ReadPointKind(string backup, string name)
    {
        string path = SafePath(SnapshotRoot(backup), name + ".kind");
        string kind = Records.ReadText(path, Records.SmallLimit, "Restore point kind").Trim();
        if (kind != "full" && kind != "incremental") throw new IOException("Invalid restore point kind.");
        return kind;
    }

    // A run changes this token before mutating files. An interrupted run or failed catalog
    // publication cannot leave a previous inventory looking current.
    internal static void BeginInventoryUpdate(string backup)
    { AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "inventory-revision.txt"), Guid.NewGuid().ToString("N")); }

    static string FileStamp(string path)
    {
        RejectRootLinks(path);
        var file = new FileInfo(path);
        return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing";
    }
    static string InventoryGeneration(string backup)
    {
        string revision = Path.Combine(AppIdentity.MetadataRoot(backup), "inventory-revision.txt");
        // A missing revision is tracked as missing metadata, so recovery can rebuild a catalog.
        string token = File.Exists(revision) ? Records.ReadText(revision, Records.SmallLimit, "Inventory revision").Trim() : "missing";
        string pointer = Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt");
        string name = Records.ReadText(pointer, Records.SmallLimit, "Current restore point").Trim();
        return "single:" + token + ":" + name;
    }
    internal static string InventoryStamp(string backup)
    {
        string generation = InventoryGeneration(backup);
        string name = Records.ReadText(Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt"), Records.SmallLimit, "Current restore point").Trim();
        return generation + ":" + FileStamp(SafePath(SnapshotRoot(backup), name + ".manifest"));
    }

    internal static CatalogData TryIncrementalInventory(string backup)
    {
        try
        {
            if (BackupStatus(backup).Unfinished) throw new IOException("backup is unfinished");
            if (!File.Exists(CatalogPath(backup))) throw new IOException("database is missing");
            CatalogData data = Read(backup, true);
            if (!IsCurrentInventory(backup, data)) throw new IOException("inventory is stale");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.Files)
            {
                OperationContext.Current.CheckCancellation();
                if (!names.Add(entry.Path) || entry.Size < 0 || entry.ModifiedTicks < 0 || entry.ModifiedTicks > DateTime.MaxValue.Ticks ||
                    entry.CreatedTicks < 0 || entry.CreatedTicks > DateTime.MaxValue.Ticks || (!entry.IsDirectory && entry.Hash == null))
                    throw new IOException("inventory has incomplete or invalid records");
            }
            OperationOutput.WriteLine("  Incremental inventory: SQLite (catalog.sqlite).");
            return data;
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex) { OperationOutput.WriteLine("  SQLite inventory unavailable: " + ex.Message + ". Using recovery records."); }
        catch (UnauthorizedAccessException ex) { OperationOutput.WriteLine("  SQLite inventory unavailable: " + ex.Message + ". Using recovery records."); }
        return null;
    }
    internal static bool IsCurrentInventory(string backup, CatalogData data)
    {
        return data.InventoryStamp != null && data.InventoryStamp == InventoryStamp(backup);
    }
}

internal sealed class CatalogData
{
    internal string DeviceId;
    internal readonly List<string> RestorePoints = new List<string>();
    internal readonly Dictionary<string, string> PointKinds = new Dictionary<string, string>(StringComparer.Ordinal);
    internal readonly Dictionary<string, DateTime> PointTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> PointNames = new Dictionary<string, string>(StringComparer.Ordinal);
    internal string InventoryStamp, InventoryGeneration;
    internal readonly List<CatalogEntry> Files = new List<CatalogEntry>();
    internal readonly List<CatalogCheckRun> Checks = new List<CatalogCheckRun>();
}
internal sealed class CatalogEntry
{
    internal string SnapshotId = "current", Path, Hash, VerifiedUtc, Result = "unchecked", ObservedHash;
    internal long Size = -1, ModifiedTicks, CreatedTicks;
    internal bool IsDirectory, Present;
}
internal sealed class CatalogVerification
{
    internal string SnapshotId = "current", Path, ExpectedHash, ObservedHash, Result, CheckedUtc = DateTime.UtcNow.ToString("O");
}
internal sealed class CatalogCheckRun { internal string CheckedUtc, Summary; }
