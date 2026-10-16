using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static GearBackup.Snapshots;
using static GearBackup.Backups;
using static GearBackup.FileCopy;
using static GearBackup.BackupFolders;
using GearBackup;
using GearBackup.Cli;

partial class Tests
{
    static CatalogEntry CurrentEntry(CatalogData data, string path)
    { return data.Files.Find(e => e.SnapshotId == "current" && e.Path == path); }

    [Test("SQLite: backup inventory and check results", Folder = "helpers")]
    static void TestBackupDatabase(string dir)
    {
        string root = Folder(dir, "sqlite-index"), source = Folder(root, "source");
        Put(Path.Combine(source, "Samples", "音 'quote'.wav"), "SAMPLE");
        Put(Path.Combine(source, "Songs", "song.m8s"), "SONG");
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        var config = new Config { Preview = false, Verify = false, MaxNameLength = 0 };
        string history = Path.Combine(root, "history");
        OperationResult result = RunBackup(source, history, null, false, config);
        string backup = ListBackups(history)[0], database = BackupDatabase.CatalogPath(backup);
        Assert(result.ExitCode == 0 && File.Exists(database), "a full backup writes its SQLite catalog");
        Assert(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(database), 0, 15) == "SQLite format 3", "catalog is a real SQLite file");
        CatalogData data = BackupDatabase.Read(backup);
        var sample = CurrentEntry(data, @"Samples\音 'quote'.wav");
        Assert(sample != null && sample.Size == 6 && sample.Hash == HashFile(Path.Combine(source, sample.Path)), "Unicode and quoted names retain their path, size and expected SHA-256");
        Assert(sample.Result == "unchecked" && sample.VerifiedUtc == null, "index creation does not claim a verification was performed");
        Assert(CurrentEntry(data, "Empty").IsDirectory && data.Files.Exists(e => e.SnapshotId != "current" && !e.IsDirectory), "empty folders and snapshot contents are indexed");
        Assert(!data.Files.Exists(e => AppIdentity.IsMetadataPath(e.Path)), "catalog does not index its own metadata");
        result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        data = BackupDatabase.Read(backup); sample = CurrentEntry(data, @"Samples\音 'quote'.wav");
        Assert(result.ExitCode == 0 && sample.Result == "ok" && sample.VerifiedUtc != null && sample.ObservedHash == sample.Hash, "integrity checks store the actual per-file results");
        Assert(data.Checks.Count == 1 && data.Files.Exists(e => e.SnapshotId != "current" && e.Result == "ok"), "check runs and verified snapshot references are recorded");
        string expected = sample.Hash;
        string objects = Path.Combine(AppIdentity.MetadataRoot(backup), "objects");
        string songHash = CurrentEntry(data, @"Songs\song.m8s").Hash;
        Put(Path.Combine(objects, sample.Hash), "BROKEN");
        File.Delete(Path.Combine(objects, songHash));
        result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        data = BackupDatabase.Read(backup); sample = CurrentEntry(data, @"Samples\音 'quote'.wav");
        Assert(result.ExitCode == 2 && sample.Result == "damaged" && sample.Hash == expected && sample.ObservedHash != expected,
            "damage detection preserves the original baseline instead of accepting damaged contents");
        Assert(CurrentEntry(data, @"Songs\song.m8s").Result == "missing" && !CurrentEntry(data, @"Songs\song.m8s").Present, "missing recorded files remain in the catalog");
        Assert(data.Checks.Count == 2, "the catalog retains completed check history");
        BackupDatabase.Rebuild(backup);
        Assert(CurrentEntry(BackupDatabase.Read(backup), sample.Path).Result == "damaged", "rebuilding preserves checks for the same expected version");
        Put(Path.Combine(source, sample.Path), "NEW CONTENT");
        Throws(() => RunBackup(source, history, backup, true, config), "an incremental backup refuses to build on damaged stored content");
        Put(Path.Combine(objects, expected), "SAMPLE"); Put(Path.Combine(objects, songHash), "SONG");
        new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        result = RunBackup(source, history, backup, true, config);
        data = BackupDatabase.Read(backup);
        Assert(result.ExitCode == 0 && CurrentEntry(data, sample.Path).Result == "unchecked", "incremental updates invalidate checks for changed content");
        string indexHash = HashFile(database);
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true }.Activate()) BackupDatabase.TryRefresh(backup);
        Assert(HashFile(database) == indexHash, "preview never rewrites the catalog");
    }

    // A long history: the catalog (built with one shared name check and a staging database without its own journal)
    // must list exactly what every restore point's manifest lists.
    [Test("SQLite: catalog of a long history", Folder = "helpers")]
    static void TestCatalogHistory(string dir)
    {
        string root = Folder(dir, "catalog-history"), source = Folder(root, "source"), history = Path.Combine(root, "history");
        var stamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Action<string, string, int> write = (relative, text, minute) => {
            string path = Path.Combine(source, relative);
            Put(path, text);
            File.SetLastWriteTimeUtc(path, stamp.AddMinutes(minute));
        };
        write(@"Songs\one.m8s", "ONE", 0); write(@"Songs\two.m8s", "TWO", 0); write(@"Samples\音 'quote'.wav", "SAMPLE", 0);
        write(@"Samples\kick.wav", "KICK", 0); write("root.txt", "ROOT", 0);
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        var config = new Config { Preview = false, Verify = false, MaxNameLength = 0 }; // the default single content store
        OperationResult first = RunBackup(source, history, null, false, config);
        string backup = first.OutputFolder;
        write(@"Songs\one.m8s", "ONE v2", 10); RunBackup(source, history, backup, true, config);
        write(@"Songs\three.m8s", "THREE", 20); RunBackup(source, history, backup, true, config);
        File.Delete(Path.Combine(source, "root.txt")); write(@"Samples\kick.wav", "KICK v2", 30); RunBackup(source, history, backup, true, config);
        RunBackup(source, history, backup, true, config); // nothing changed: still a restore point
        Assert(first.ExitCode == 0 && ListBackups(history).Count == 1, "the history is one backup with several restore points");

        CatalogData data = BackupDatabase.Read(backup);
        string snapshots = SnapshotRoot(backup);
        string[] manifests = Directory.GetFiles(snapshots, "*.manifest");
        Assert(manifests.Length == 5 && data.RestorePoints.Count == 5, "every restore point is in the catalog");
        foreach (string manifest in manifests)
        {
            Snapshot expected = ReadSnapshot(backup, manifest);
            var files = data.Files.FindAll(e => e.SnapshotId == expected.Name && !e.IsDirectory);
            var folders = data.Files.FindAll(e => e.SnapshotId == expected.Name && e.IsDirectory);
            bool same = files.Count == expected.Files.Count && folders.Count == expected.Folders.Count;
            foreach (RestoreEntry item in expected.Files)
            {
                var row = files.Find(e => e.Path == item.Relative);
                same &= row != null && row.Hash == item.Hash && row.Size == item.Size && row.ModifiedTicks == item.ModifiedTicks && row.Present;
            }
            foreach (string folder in expected.Folders) same &= folders.Exists(e => e.Path == folder);
            Assert(same, "the catalog lists exactly the files and folders of restore point " + expected.Name);
        }
        Snapshot now = ContentStore.Current(backup);
        Assert(data.Files.FindAll(e => e.SnapshotId == "current" && !e.IsDirectory).Count == now.Files.Count && now.Files.Count == 5 &&
            CurrentEntry(data, @"Songs\three.m8s") != null && CurrentEntry(data, "root.txt") == null, "the current listing matches the newest restore point");
        Assert(Directory.GetFiles(AppIdentity.MetadataRoot(backup), ".gearcatalog-*").Length == 0, "no staging file is left behind");
        BackupDatabase.Rebuild(backup);
        CatalogData rebuilt = BackupDatabase.Read(backup);
        Assert(rebuilt.Files.Count == data.Files.Count && rebuilt.RestorePoints.Count == 5, "rebuilding an existing catalog gives the same rows");

        // A rebuild reads only the rows that carry a recorded check; everything it keeps must survive.
        Assert(BackupDatabase.Read(backup, false, null, true).Files.Count == 0, "a catalog without checks has no verified rows");
        new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        CatalogData verified = BackupDatabase.Read(backup);
        int okRows = verified.Files.FindAll(e => e.Result == "ok").Count;
        CatalogData verifiedOnly = BackupDatabase.Read(backup, false, null, true);
        Assert(okRows > 0 && verifiedOnly.Files.Count == verified.Files.FindAll(e => e.Result != "unchecked" || e.VerifiedUtc != null || e.ObservedHash != null).Count &&
            verifiedOnly.RestorePoints.Count == 0 && verifiedOnly.Checks.Count == verified.Checks.Count, "the verified-only read returns just checked rows and the check runs");
        BackupDatabase.Rebuild(backup);
        CatalogData after = BackupDatabase.Read(backup);
        bool kept = after.Files.Count == verified.Files.Count && after.Checks.Count == verified.Checks.Count && after.RestorePoints.Count == 5;
        foreach (CatalogEntry entry in verified.Files)
        {
            var again = after.Files.Find(e => e.SnapshotId == entry.SnapshotId && e.Path == entry.Path);
            kept &= again != null && again.Result == entry.Result && again.VerifiedUtc == entry.VerifiedUtc && again.ObservedHash == entry.ObservedHash;
        }
        Assert(kept, "a rebuild keeps every recorded check and the check history exactly");

        // A name found valid is remembered across manifests, but a bad name is still refused and never remembered.
        string header = AppIdentity.SnapshotHeader, hash = new string('a', 64);
        string good = Path.Combine(snapshots, "zz-good.manifest"), bad = Path.Combine(snapshots, "zz-bad.manifest"), dup = Path.Combine(snapshots, "zz-dup.manifest");
        Func<string, string> manifestRow = path => "F\t" + Snapshots.Encode(path) + "\t" + hash + "\t1\t1\t1\n";
        File.WriteAllText(good, header + "\n" + manifestRow(@"Songs\x.m8s"));
        File.WriteAllText(bad, header + "\n" + manifestRow(@"Songs\x.m8s") + manifestRow(@"..\evil"));
        File.WriteAllText(dup, header + "\n" + manifestRow(@"Songs\x.m8s") + manifestRow(@"Songs\x.m8s"));
        foreach (string fake in new[] { good, bad, dup }) File.Copy(Path.ChangeExtension(manifests[0], ".taken"), Path.ChangeExtension(fake, ".taken")); // a restore point also has its time
        var names = new HashSet<string>(StringComparer.Ordinal);
        ReadSnapshot(backup, good, null, names);
        Assert(names.Contains(@"Songs\x.m8s"), "a validated name is remembered");
        Throws(() => ReadSnapshot(backup, bad, null, names), "a path that escapes its folder is refused even when other names were remembered");
        Assert(!names.Contains(@"..\evil"), "an invalid name is never remembered");
        Throws(() => ReadSnapshot(backup, dup, null, names), "a duplicate path is still refused with remembered names");
        foreach (string fake in new[] { good, bad, dup }) { File.Delete(fake); File.Delete(Path.ChangeExtension(fake, ".taken")); }
    }

    // After a backup run the catalog is updated from the previous one instead of rebuilt; both must list the same things.
    [Test("SQLite: updated catalog equals a rebuilt one", Folder = "helpers")]
    static void TestCatalogUpdateEqualsRebuild(string dir)
    {
        string root = Folder(dir, "catalog-update"), source = Folder(root, "source"), history = Path.Combine(root, "history");
        Put(Path.Combine(source, "one.txt"), "ONE"); Put(Path.Combine(source, "Sub", "two.txt"), "TWO");
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        var config = new Config { Preview = false, Verify = false, MaxNameLength = 0 };
        string backup = RunBackup(source, history, null, false, config).OutputFolder;
        new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        Put(Path.Combine(source, "one.txt"), "ONE CHANGED");
        config.IncrementalBackupName = "Second";
        RunBackup(source, history, backup, true, config);
        config.IncrementalBackupName = null;
        File.Delete(Path.Combine(source, "Sub", "two.txt")); Put(Path.Combine(source, "three.txt"), "THREE");
        RunBackup(source, history, backup, true, config);
        RunBackup(source, history, backup, true, config);
        CatalogData updated = BackupDatabase.Read(backup);
        Assert(CurrentEntry(updated, "Sub" + (char)92 + "two.txt") == null && CurrentEntry(updated, "three.txt") != null, "the updated catalog lists the current files");
        Assert(CurrentEntry(updated, "Empty").IsDirectory, "folders are kept");
        Assert(updated.RestorePoints.Count == 4 && updated.Checks.Count == 1, "restore points and the check history are carried forward");
        Assert(updated.PointNames.ContainsValue("Second"), "an incremental name is recorded");
        File.Delete(BackupDatabase.CatalogPath(backup));
        BackupDatabase.Rebuild(backup);
        CatalogData rebuilt = BackupDatabase.Read(backup);
        Func<CatalogData, List<string>> rows = data => {
            var list = new List<string>();
            foreach (var e in data.Files) list.Add(e.SnapshotId + "|" + e.Path + "|" + e.IsDirectory + "|" + e.Hash + "|" + e.Size + "|" + e.ModifiedTicks + "|" + e.Present);
            list.Sort(StringComparer.Ordinal);
            return list;
        };
        Assert(string.Join(";", rows(updated)) == string.Join(";", rows(rebuilt)), "an updated catalog lists the same rows as a rebuilt one");
        Assert(string.Join(",", updated.RestorePoints) == string.Join(",", rebuilt.RestorePoints) && updated.PointKinds.Count == rebuilt.PointKinds.Count &&
            updated.PointTimes.Count == rebuilt.PointTimes.Count && updated.PointNames.Count == rebuilt.PointNames.Count, "restore point metadata matches");
    }

    [Test("SQLite: rebuild, interruption and links", Folder = "helpers")]
    static void TestBackupDatabaseSafety(string dir)
    {
        string root = Folder(dir, "sqlite-safety"), source = Folder(root, "source"), history = Path.Combine(root, "history");
        string victim = Path.Combine(root, "victim.txt");
        Put(victim, "UNRELATED"); Put(Path.Combine(source, "keep.txt"), "ORIGINAL");
        var config = new Config { Preview = false };
        string backup = RunBackup(source, history, null, false, config).OutputFolder;
        BackupDatabase.Rebuild(backup);
        string database = BackupDatabase.CatalogPath(backup);
        string expected = CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Hash;
        File.WriteAllText(database, "CORRUPT DATABASE");
        BackupDatabase.Rebuild(backup);
        Assert(CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Hash == expected, "a corrupt catalog is rebuilt from the restore points");
        string original = HashFile(database);
        using (var held = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => BackupDatabase.Rebuild(backup), "locked publication fails without truncating the old catalog");
        Assert(HashFile(database) == original && Directory.GetFiles(Path.GetDirectoryName(database), ".gearcatalog-*").Length == 0, "failed publication preserves the database and cleans staging files");
        var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        using (new OperationContext(new TestInteraction(), cancellation.Token).Activate())
            try { BackupDatabase.Rebuild(backup); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && HashFile(database) == original, "cancelled rebuild preserves the previous database");
        File.Delete(database);
        Assert(TryHardLink(victim, database), "catalog hard-link fixture created");
        BackupDatabase.Rebuild(backup);
        Assert(File.ReadAllText(victim) == "UNRELATED", "catalog replacement does not truncate an unrelated hard-linked file");
        File.Delete(database);
        CreateTestFileSymlink(database, victim);
        try
        {
            Throws(() => BackupDatabase.Read(backup), "linked catalog reads are rejected");
            Throws(() => BackupDatabase.Rebuild(backup), "linked catalog writes are rejected");
            Assert(File.ReadAllText(victim) == "UNRELATED", "linked catalog leaves outside contents intact");
        }
        finally { File.Delete(database); }
        BackupDatabase.Rebuild(backup);
        using (var db = new SqliteDatabase(database, false)) db.Execute("PRAGMA user_version=999");
        BackupDatabase.Rebuild(backup);
        Assert(CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Hash == expected, "unknown database versions are rebuilt without trusting their baseline");
        File.Delete(database);
        BackupDatabase.Rebuild(backup);
        Assert(CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Hash == expected, "a missing catalog is rebuilt from the restore points");

        var result = RunBackup(source, history, backup, true, config);
        Assert(result.ExitCode == 0 && File.Exists(database), "an incremental backup keeps the catalog");
        File.Delete(database);
        result = RunBackup(source, history, backup, true, config);
        Assert(result.ExitCode == 0 && File.Exists(database), "an unchanged incremental creates a missing catalog");
    }
}
