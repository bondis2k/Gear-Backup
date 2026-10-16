using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static Backups;
using static Snapshots;

partial class Tests
{
    [Test("SQLite: device-filtered full and individual restores", Folder = "helpers")]
    static void TestRestoreCatalog(string dir)
    {
        TestRestoreInventoryPathScope(dir);
        TestRestorePointClockNames(dir);
        foreach (bool single in new[] { false, true })
        {
            string root = Folder(dir, "restore-catalog-" + single), source = Folder(root, "source");
            Put(Path.Combine(source, "Songs", "one.m8s"), "FULL");
            Put(Path.Combine(source, "Songs", "Other", "two.m8s"), "OTHER");
            Put(Path.Combine(source, "Samples", "kick.wav"), "SAMPLE");
            Directory.CreateDirectory(Path.Combine(source, "Songs", "Empty", "Nested"));
            var config = new Config { BackupFolder = root, Preview = false, FullBackupName = "Live" };
            string history = Path.Combine(Session.HistoriesRoot(config), "profiles", "Studio");
            string backup;
            using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.M8 }.Activate())
                backup = RunBackup(source, history, null, false, config).OutputFolder;
            Put(Path.Combine(source, "Songs", "one.m8s"), "FIRST INCREMENTAL");
            RunBackup(source, history, backup, true, config);
            File.Delete(Path.Combine(source, "Songs", "Other", "two.m8s"));
            Put(Path.Combine(source, "Songs", "one.m8s"), "LATEST INCREMENTAL CONTENT");
            RunBackup(source, history, backup, true, config);
            string otherHistory = Path.Combine(Session.HistoriesRoot(config), "profiles", "OtherDevice");
            using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.OP1Field }.Activate())
                RunBackup(source, otherHistory, null, false, config);
            Assert(RestoreCatalog.BackupsForDevice(config, Devices.M8).Count == 1 && RestoreCatalog.BackupsForDevice(config, null).Count == 2,
                "restore backups are filtered with a detected device and include all types without one");
            Assert(RestoreCatalog.BackupsForDevice(config, Devices.OP1Field).Count == 1, "other device backups remain separately available");
            RestoreSet set = RestoreCatalog.Load(backup, Devices.M8);
            var offlineSet = RestoreCatalog.Load(backup, null);
            Assert(offlineSet.Device == Devices.M8 && offlineSet.Sqlite && offlineSet.Points.Count == set.Points.Count,
                "offline restore reads the backup's device identity and SQLite points in both layouts");
            string otherBackup = RestoreCatalog.BackupsForDevice(config, Devices.OP1Field)[0];
            var otherSet = RestoreCatalog.Load(otherBackup, null);
            var otherPoint = otherSet.Points[0];
            var offlineRequest = new RestoreRequest { Backup = otherBackup, DeviceId = otherSet.Device.Id, Point = otherPoint.Id, Stamp = RestoreCatalog.Stamp(otherPoint.Snapshot) };
            string offlineTarget = Folder(root, "offline-other-device");
            using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
                Assert(RestoreCatalog.Run(offlineTarget, config, offlineRequest).ExitCode == 0 &&
                    File.ReadAllText(Path.Combine(offlineTarget, "Songs", "one.m8s")) == "LATEST INCREMENTAL CONTENT",
                    "offline restore uses the selected non-default device's backup without a connected source");
            Assert(set.Sqlite && set.Points.Count == 4 && set.Points[0].Id != "current" && set.Points[2].Label.StartsWith("Full backup"),
                "SQLite lists full and incremental points newest first in both storage layouts");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(set.Points[0].Id, @"^\d{4}-\d{2}-\d{2}_\d{2}\.\d{2}\.\d{2}(_\d+)?$") &&
                set.Points[0].Label == "Incremental · " + set.Points[0].Id, "incremental points use date and time names in both layouts and GUI labels");
            string firstId = set.Points[2].Id;
            if (!single)
            {
                File.Delete(Path.Combine(AppIdentity.MetadataRoot(backup), "old-versions.txt"));
                TestInteraction inventoryUi = WithUi(() => set = RestoreCatalog.Load(backup, Devices.M8));
                Assert(set.Sqlite && !inventoryUi.Progress.Exists(p => p.Stage == Stage.ScanningBackup),
                    "restore picker does not scan user files when an optional version record is absent");
            }
            string manifest = Path.Combine(SnapshotRoot(backup), firstId + ".manifest");
            using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert(RestoreCatalog.Load(backup, Devices.M8).Sqlite, "restore browsing uses SQLite without parsing full-point manifests");
            using (var db = new SqliteDatabase(BackupDatabase.CatalogPath(backup), false)) db.Execute("DROP TABLE restore_points");
            Throws(() => BackupDatabase.Read(backup), "catalogs missing current restore-point schema are rejected");
            BackupDatabase.Rebuild(backup);
            using (var db = new SqliteDatabase(BackupDatabase.CatalogPath(backup), false)) db.Execute("DELETE FROM metadata WHERE key LIKE 'point_kind:%'");
            Throws(() => BackupDatabase.Read(backup), "saved runs require explicit full/incremental metadata");
            BackupDatabase.Rebuild(backup);
            var point = set.Points[0];
            var request = new RestoreRequest { Backup = backup, Point = point.Id, DeviceId = Devices.M8.Id, Stamp = RestoreCatalog.Stamp(point.Snapshot) };
            string target = Folder(root, "latest-target");
            var result = RestoreCatalog.Run(target, config, request);
            Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "LATEST INCREMENTAL CONTENT" &&
                !File.Exists(Path.Combine(target, "Songs", "Other", "two.m8s")), "default latest saved run restores exact file membership through merge without resurrecting deleted files");
            Assert(Directory.Exists(Path.Combine(target, "Songs", "Empty", "Nested")), "full restore preserves empty folders");
            request.Point = firstId; request.Stamp = RestoreCatalog.Stamp(set.Points[2].Snapshot);
            target = Folder(root, "full-target");
            result = RestoreCatalog.Run(target, config, request);
            Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "FULL", "earliest full run remains restorable after multiple incrementals");
            request.Paths = new[] { "Songs" };
            target = Folder(root, "individual-target");
            Put(Path.Combine(target, "keep.txt"), "KEEP");
            result = RestoreCatalog.Run(target, config, request);
            Assert(result.ExitCode == 0 && File.Exists(Path.Combine(target, "Songs", "Other", "two.m8s")) &&
                !Directory.Exists(Path.Combine(target, "Samples")) && File.ReadAllText(Path.Combine(target, "keep.txt")) == "KEEP", "selected folder restores its subtree and leaves unrelated files alone");
            request.Paths = new[] { @"Songs\Empty\Nested" };
            target = Folder(root, "empty-target");
            result = RestoreCatalog.Run(target, config, request);
            Assert(result.ExitCode == 0 && Directory.Exists(Path.Combine(target, "Songs", "Empty", "Nested")) && Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 0,
                "individual empty folder restores with ancestors and no unrelated files");
            request.Paths = null; request.Exact = true;
            Put(Path.Combine(target, "extra.txt"), "EXTRA");
            WithUi(() => result = RestoreCatalog.Run(target, config, request), 'y');
            Assert(result.ExitCode == 0 && !File.Exists(Path.Combine(target, "extra.txt")), "full saved-point restore can still remove extras after explicit confirmation");
            request.Paths = new[] { @"Songs\one.m8s" };
            Throws(() => RestoreCatalog.Run(target, config, request), "individual restore cannot request exact removal");
            request.Exact = false;
            request.Paths = new[] { @"Songs\one.m8s" }; request.DeviceId = Devices.OP1Field.Id;
            Throws(() => RestoreCatalog.Run(target, config, request), "operation rechecks device match instead of trusting the UI");
            request.DeviceId = Devices.M8.Id; request.Stamp = "old selection";
            Throws(() => RestoreCatalog.Run(target, config, request), "changed restore inventory is rejected before target mutation");
            request.Stamp = RestoreCatalog.Stamp(set.Points[2].Snapshot); request.Paths = new[] { "not-in-backup" };
            Throws(() => RestoreCatalog.Run(target, config, request), "unknown selected paths are rejected");
            request.Paths = null;
            Throws(() => RestoreCatalog.Run(backup, config, request), "restore cannot overwrite its own backup");
            string database = BackupDatabase.CatalogPath(backup);
            File.WriteAllText(database, "CORRUPT");
            set = RestoreCatalog.Load(backup, Devices.M8);
            Assert(!set.Sqlite && set.Points.Count == 4 && File.ReadAllText(database) == "CORRUPT", "read-only restore browsing falls back to recovery records without repairing database");
            string unknown = Folder(root, "2026-01-01");
            Throws(() => RestoreCatalog.Load(unknown, Devices.M8), "unknown device backup cannot enter a device-filtered restore");
            Throws(() => RestoreCatalog.Load(unknown, null), "offline restore still rejects unknown backup identity");
        }
        string plain = Folder(dir, "restore-current-only"), plainSource = Folder(plain, "source");
        Put(Path.Combine(plainSource, "one.txt"), "ORIGINAL");
        var plainConfig = new Config { BackupFolder = plain, Preview = false };
        string plainHistory = Path.Combine(Session.HistoriesRoot(plainConfig), "profiles", "Plain");
        string first = RunBackup(plainSource, plainHistory, null, false, plainConfig).OutputFolder;
        string second = RunBackup(plainSource, plainHistory, first, false, plainConfig).OutputFolder;
        RunBackup(plainSource, plainHistory, first, true, plainConfig);
        var latest = RestoreCatalog.BackupsForDevice(plainConfig, Devices.M8);
        Assert(latest[0] == first && latest.Count == 2, "restore defaults to most recent successful run even when an older full set was updated");
        var plainSet = RestoreCatalog.Load(first, Devices.M8);
        Assert(plainSet.Sqlite && plainSet.Points.Count == 3, "every run of a backup is offered as a restore point, after the latest files");
    }

    static void TestRestoreInventoryPathScope(string dir)
    {
        string root = Folder(dir, "restore-path-scope"), outside = Folder(dir, "restore-path-outside");
        string child = Folder(root, "Songs"), file = Path.Combine(child, "one.m8s");
        Put(file, "SONG"); Put(Path.Combine(outside, "one.m8s"), "SONG");
        var paths = new InventoryPathCache(root);
        string resolved = paths.Resolve(@"Songs\one.m8s");
        Assert(resolved == paths.Resolve(@"Songs\one.m8s"), "repeated inventory rows resolve to the same safe source");
        foreach (string bad in new[] { @"..\outside", @"Songs\..\one.m8s", @"Songs\one.m8s:stream", @"Songs\one.m8s.", @"C:\outside" })
            Throws(() => paths.Resolve(bad), "inventory path cache still rejects noncanonical or escaping paths");
        File.Delete(file); Directory.Delete(child);
        CreateTestJunction(child, outside);
        try
        {
            Throws(() => new InventoryPathCache(root).Resolve(@"Songs\one.m8s"), "a new inventory scope rechecks links");
            Throws(() => RestoreFlow.ValidateEntry(new RestoreEntry { StoredPath = resolved, Relative = @"Songs\one.m8s", Size = 4 }, false),
                "restore rechecks cached source paths before opening files even when checksum verification is off");
        }
        finally { Directory.Delete(child); }
    }

    static void TestRestorePointClockNames(string dir)
    {
        string root = Folder(dir, "clock-point-names"), source = PathSafety.Rooted(Folder(root, "source"));
        Put(Path.Combine(source, "one.m8s"), "SONG");
        var config = new Config { BackupFolder = root, Preview = false };
        string backup = RunBackup(source, Path.Combine(Session.HistoriesRoot(config), "profiles", "Clock"), null, false, config).OutputFolder;
        string full = Path.GetFileNameWithoutExtension(Directory.GetFiles(SnapshotRoot(backup), "*.manifest")[0]);
        var started = new DateTime(2026, 10, 1, 23, 59, 58, DateTimeKind.Local);
        RestorePointNames.SaveMetadata(backup, full, started.AddHours(-1), false);
        string template = Path.Combine(SnapshotRoot(backup), full + ".manifest");
        Action<DateTime> addPoint = when => {
            string name = RestorePointNames.Create(backup, when, true);
            File.Copy(template, Path.Combine(SnapshotRoot(backup), name + ".manifest"));
            RestorePointNames.SaveMetadata(backup, name, when, true);
        };
        for (int i = 0; i < 10; i++) addPoint(started);
        Assert(File.Exists(Path.Combine(SnapshotRoot(backup), "2026-10-01_23.59.58.manifest")) && File.Exists(Path.Combine(SnapshotRoot(backup), "2026-10-01_23.59.58_10.manifest")),
            "same-second captures receive safe suffixes instead of overwriting saved runs");
        BackupDatabase.Rebuild(backup);
        Assert(RestoreCatalog.Load(backup, Devices.M8).Points[0].Id == "2026-10-01_23.59.58_10", "same-instant collisions sort numerically, including suffix ten");
        var midnight = new DateTime(2026, 10, 2, 0, 0, 1, DateTimeKind.Local);
        addPoint(midnight);
        BackupDatabase.Rebuild(backup);
        RestoreSet set = RestoreCatalog.Load(backup, Devices.M8);
        Assert(set.Points[0].Id == "2026-10-02_00.00.01" && set.Points[0].Snapshot.TakenUtc == midnight.ToUniversalTime(),
            "latest defaults use capture metadata across midnight, not filename order or source file dates");
        Assert(Path.GetFileNameWithoutExtension(RestorePointNames.OrderedManifests(backup)[0]) == "2026-10-02_00.00.01", "CLI uses the same chronological ordering as SQLite GUI");
        using (var locked = new FileStream(Path.Combine(SnapshotRoot(backup), "2026-10-02_00.00.01.taken"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert(RestoreCatalog.Load(backup, Devices.M8).Sqlite, "GUI capture timestamps come from SQLite without rereading sidecar files");
        addPoint(started.AddDays(1));
        BackupDatabase.Rebuild(backup);
        Assert(RestoreCatalog.Load(backup, Devices.M8).Points[0].Id == "2026-10-02_23.59.58", "the same clock time on another day is a separate, later point");
    }

    // An incremental backup can be given a name, shown beside its date and time in both storage layouts.
    [Test("Named incremental backups", Folder = "helpers")]
    static void TestIncrementalBackupNames(string dir)
    {
        foreach (bool single in new[] { true })
        {
            string root = Folder(dir, "incremental-names-" + single), source = Folder(root, "source");
            Put(Path.Combine(source, "one.m8s"), "FULL");
            var config = new Config { BackupFolder = root, Preview = false };
            string history = Path.Combine(Session.HistoriesRoot(config), "profiles", "Names"), backup;
            using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.M8 }.Activate())
                backup = RunBackup(source, history, null, false, config).OutputFolder;
            Put(Path.Combine(source, "one.m8s"), "FIRST");
            config.IncrementalBackupName = "  Studio jam  ";
            RunBackup(source, history, backup, true, config);
            Put(Path.Combine(source, "one.m8s"), "SECOND");
            config.IncrementalBackupName = null;
            RunBackup(source, history, backup, true, config);
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1) BackupDatabase.Rebuild(backup);
                RestoreSet set = RestoreCatalog.Load(backup, Devices.M8);
                Assert(set.Sqlite && set.Points.Count == 4, "named and unnamed incremental points are listed (layout single=" + single + ")");
                Assert(set.Points[1].Label == "Incremental · Studio jam · " + set.Points[1].Id, "an incremental name appears beside its date and time");
                Assert(set.Points[0].Label == "Incremental · " + set.Points[0].Id, "an unnamed incremental point shows only its date and time");
            }
            string named = Path.Combine(SnapshotRoot(backup), RestoreCatalog.Load(backup, Devices.M8).Points[1].Id + ".manifest");
            Assert(ReadSnapshot(backup, named).CustomName == "Studio jam", "the name is saved with the restore point, trimmed");
            config.IncrementalBackupName = new string('x', 81);
            Throws(() => RunBackup(source, history, backup, true, config), "an incremental name over 80 characters is refused");
        }
    }
}
