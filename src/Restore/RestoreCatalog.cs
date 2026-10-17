using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using static GearBackup.PathSafety;
using static GearBackup.BackupStatuses;
using static GearBackup.Histories;
using static GearBackup.DeviceSelection;
using static GearBackup.Snapshots;
using static GearBackup.BackupFolders;

namespace GearBackup
{
    // The GUI's restore inventory, optionally filtered by the detected device. SQLite supplies paths and saved run membership;
    // manifests and checksum files are read only when the catalog cannot safely supply them.
    internal static class RestoreCatalog
    {
        internal static List<string> BackupsForDevice(Config config, DeviceProfile device, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            var result = new List<string>();
            var times = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string history in AvailableHistories(config, context)) foreach (string backup in BackupFolders.ListBackups(history))
            {
                context.CheckCancellation();
                try
                {
                    DeviceProfile recorded = BackupDevice(backup);
                    if (recorded != null && (device == null || recorded.Id == device.Id))
                    {
                        string success = Path.Combine(AppIdentity.MetadataRoot(backup), "last-success.txt");
                        DateTime time;
                        times[backup] = File.Exists(success) && DateTime.TryParseExact(Records.ReadText(success, Records.SmallLimit, "Last successful backup").Trim(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)
                            ? time.ToUniversalTime().Ticks : DateTime.TryParseExact(Path.GetFileName(backup).Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out time) ? time.Ticks : 0;
                        result.Add(backup);
                    }
                }
                catch (IOException ex) { context.WriteLine("  Skipping unreadable backup device record: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { context.WriteLine("  Skipping unreadable backup device record: " + ex.Message); }
            }
            result.Sort((a, b) => {
                int recent = times[b].CompareTo(times[a]);
                if (recent != 0) return recent;
                int date = StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(b), Path.GetFileName(a));
                return date != 0 ? date : StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });
            return result;
        }

        internal static RestoreSet Load(string backup, DeviceProfile device, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            RejectRootLinks(backup);
            DeviceProfile recorded = BackupDevice(backup);
            if (recorded == null) throw new IOException("This backup has no recognized device record.");
            if (device != null && recorded.Id != device.Id)
                throw new IOException("This backup does not match the identified device. Detect the device and choose its backup again.");
            var set = new RestoreSet { Backup = backup, Device = recorded };
            ContentStore.RequireContentStore(backup);
            try
            {
                LoadSqlite(set, backup, recorded, context);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) { set.Sqlite = false; set.Points.Clear(); set.Notice = "SQLite unavailable: " + ex.Message + ". Showing recovery records."; }
            catch (UnauthorizedAccessException ex) { set.Sqlite = false; set.Points.Clear(); set.Notice = "SQLite unavailable: " + ex.Message + ". Showing recovery records."; }
            if (!set.Sqlite)
            {
                LoadRecoveryRecords(set, backup, context);
            }
            if (set.Sqlite) set.Notice = "Inventory: SQLite.";
            return set;
        }

        static void LoadSqlite(RestoreSet set, string backup, DeviceProfile recorded, OperationContext context)
        {
            var inventoryPaths = new InventoryPathCache(backup);
            var objectPaths = new InventoryPathCache(Path.Combine(AppIdentity.MetadataRoot(backup), "objects"));
            CatalogData data = BackupDatabase.Read(backup, false, inventoryPaths);
            if (BackupStatus(backup).Unfinished || data.DeviceId != recorded.Id || !BackupDatabase.IsCurrentInventory(backup, data))
                throw new IOException("catalog is stale or incomplete");
            set.Sqlite = true;
            var grouped = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
            grouped.Add("current", new Snapshot { Name = "Latest backup files" });
            foreach (string name in data.RestorePoints)
            {
                if (name == "current") throw new IOException("invalid saved restore point name");
                string customName;
                data.PointNames.TryGetValue(name, out customName);
                grouped.Add(name, new Snapshot { Name = name, TakenUtc = data.PointTimes[name], CustomName = customName });
            }
            foreach (var entry in data.Files)
            {
                context.CheckCancellation();
                if (AppIdentity.IsMetadataPath(inventoryPaths.Resolve(entry.Path))) throw new IOException("catalog contains internal metadata paths");
                if (entry.Size < 0 || entry.ModifiedTicks < 0 || entry.ModifiedTicks > DateTime.MaxValue.Ticks ||
                    entry.CreatedTicks < 0 || entry.CreatedTicks > DateTime.MaxValue.Ticks) throw new IOException("invalid catalog metadata");
                Snapshot snapshot;
                if (!grouped.TryGetValue(entry.SnapshotId, out snapshot)) throw new IOException("catalog contains an unlisted restore point");
                if (entry.IsDirectory) snapshot.Folders.Add(entry.Path);
                else
                {
                    if (!ValidHash(entry.Hash)) throw new IOException("Invalid snapshot checksum.");
                    string stored = objectPaths.Resolve(entry.Hash);
                    snapshot.Files.Add(new RestoreEntry { Relative = entry.Path, StoredPath = stored, Hash = entry.Hash,
                        Size = entry.Size, ModifiedTicks = entry.ModifiedTicks, CreatedTicks = entry.CreatedTicks });
                }
            }
            AddPoints(set, grouped, data.PointKinds);
        }

        static void LoadRecoveryRecords(RestoreSet set, string backup, OperationContext context)
        {
            var grouped = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
            var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
            grouped.Add("current", Snapshots.CurrentBackup(backup));
            string root = SnapshotRoot(backup);
            RejectRootLinks(root);
            var validNames = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(root)) foreach (string manifest in Directory.GetFiles(root, "*.manifest"))
            {
                context.CheckCancellation();
                Snapshot snapshot = ReadSnapshot(backup, manifest, null, validNames);
                grouped.Add(snapshot.Name, snapshot);
                kinds[snapshot.Name] = BackupDatabase.ReadPointKind(backup, snapshot.Name);
            }
            AddPoints(set, grouped, kinds);
        }

        static void AddPoints(RestoreSet set, Dictionary<string, Snapshot> grouped, Dictionary<string, string> kinds)
        {
            var names = new List<string>(grouped.Keys); names.Remove("current");
            names.Sort((a, b) => { int order = grouped[b].TakenUtc.CompareTo(grouped[a].TakenUtc); return order != 0 ? order : RestorePointNames.CompareNames(b, a); });
            foreach (string name in names)
            {
                string kind;
                if (!kinds.TryGetValue(name, out kind) || (kind != "full" && kind != "incremental")) throw new IOException("Missing or invalid restore point kind.");
                set.Points.Add(new RestorePoint { Id = name, Snapshot = grouped[name], Label = kind == "full"
                    ? "Full backup · " + grouped[name].TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : "Incremental · " + (grouped[name].CustomName == null ? "" : grouped[name].CustomName + " · ") + name });
            }
            set.Points.Add(new RestorePoint { Id = "current", Snapshot = grouped["current"], Label = names.Count == 0 ? "Latest backup files (no saved restore points)" : "Current backup files" });
        }

        internal static string Stamp(Snapshot snapshot)
        {
            var records = new List<string>();
            foreach (var file in snapshot.Files) records.Add("F\t" + Encode(file.Relative) + "\t" + file.Hash + "\t" + file.Size + "\t" + file.ModifiedTicks + "\t" + file.CreatedTicks);
            foreach (string folder in snapshot.Folders) records.Add("D\t" + Encode(folder));
            records.Sort(StringComparer.Ordinal);
            using (var hash = System.Security.Cryptography.SHA256.Create()) return FileCopy.ToHex(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", records))));
        }

        internal static Snapshot Select(Snapshot snapshot, string[] paths)
        {
            var result = new Snapshot { Name = snapshot.Name };
            var selected = new HashSet<string>(paths ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(snapshot.Folders, StringComparer.OrdinalIgnoreCase);
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in snapshot.Files) known.Add(file.Relative);
            foreach (string path in selected) if (!known.Contains(path)) throw new IOException("A selected file or folder is no longer in this restore point: " + path);
            foreach (string path in selected)
                for (string parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent)) parents.Add(parent);
            foreach (var file in snapshot.Files)
                if (Selected(file.Relative, selected)) result.Files.Add(file);
            foreach (string folder in snapshot.Folders)
                if (Selected(folder, selected) || parents.Contains(folder)) result.Folders.Add(folder);
            return result;
        }
        static bool Selected(string path, HashSet<string> selected)
        {
            if (selected.Contains(path)) return true;
            for (string parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (selected.Contains(parent)) return true;
            return false;
        }

        internal static OperationResult Run(string target, Config config, RestoreRequest request, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            DeviceProfile device = Devices.Find(request.DeviceId);
            if (device == null) throw new IOException("The selected backup has no recognized device type. Refresh the restore selection.");
            if (!BackupsForDevice(config, device, context).Exists(path => Rooted(path) == Rooted(request.Backup)))
                throw new IOException("The selected backup is no longer available for the identified device.");
            string history = Path.GetDirectoryName(request.Backup);
            using (Histories.LockUnlessPreview(history, context))
            using (context.UseDevice(device))
            {
                RestoreFlow.ValidateRestoreDestination(request.Backup, target, config);
                RestoreSet set = Load(request.Backup, device, context);
                RestorePoint point = set.Points.Find(p => p.Id == request.Point);
                if (point == null || Stamp(point.Snapshot) != request.Stamp) throw new IOException("The selected backup changed. Refresh the restore selection and review the files again.");
                if (BackupStatus(request.Backup).Unfinished && !context.AskYesNo("This backup is unfinished. Restore its available files anyway?")) return new OperationResult(OperationOutcome.Cancelled);
                Snapshot snapshot = request.Paths == null ? Select(point.Snapshot, point.Snapshot.Files.ConvertAll(f => f.Relative).ToArray()) : Select(point.Snapshot, request.Paths);
                if (request.Paths == null) snapshot.Folders = new List<string>(point.Snapshot.Folders);
                if (snapshot.Files.Count == 0 && snapshot.Folders.Count == 0) throw new IOException("No files or folders selected.");
                if (request.Exact && request.Paths != null) throw new IOException("Exact restore requires a complete saved restore point and full-backup restore.");
                Config effective = ProfileSettings.ResolveProfileSettings(config, device.Id, Path.GetFileName(history));
                RestoreFlow.ValidateRestoreDestination(request.Backup, target, effective);
                if (!RestoreFlow.ConfirmRestoreTarget(request.Backup, target, context)) return new OperationResult(OperationOutcome.Cancelled);
                context.WriteLine("  Restore inventory: " + (set.Sqlite ? "SQLite (catalog.sqlite)." : "recovery records."));
                context.WriteLine("  Selected backup: " + BackupSetNames.Label(request.Backup) + " · " + point.Label);
                return new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, RestoreMode.All, null, request.Exact, effective, context,
                    () => RestoreFlow.ValidateRestoreDestination(request.Backup, target, effective));
            }
        }
    }
    internal sealed class RestoreSet { internal string Backup, Notice; internal DeviceProfile Device; internal bool Sqlite; internal readonly List<RestorePoint> Points = new List<RestorePoint>(); }
    internal sealed class RestorePoint { internal string Id, Label; internal Snapshot Snapshot; public override string ToString() { return Label; } }

}
