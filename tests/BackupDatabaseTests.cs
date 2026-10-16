using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static Checksums;
using static Snapshots;
using static Backups;
using static Workflow;
using static FileCopy;

partial class Tests
{
    static CatalogEntry CurrentEntry(CatalogData data, string path)
    { return data.Files.Find(e => e.SnapshotId == "current" && e.Path == path); }

    static void TestBackupDatabase(string dir)
    {
        string root = Folder(dir, "sqlite-index"), source = Folder(root, "source");
        Put(Path.Combine(source, "Samples", "音 'quote'.wav"), "SAMPLE");
        Put(Path.Combine(source, "Songs", "song.m8s"), "SONG");
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        var config = new Config { SingleContentStore = false, Preview = false, PointInTimeHistory = true, Verify = false, MaxNameLength = 0 };
        string history = Path.Combine(root, "history");
        OperationResult result = RunBackup(source, history, null, false, config);
        string backup = ListBackups(history)[0], database = BackupDatabase.CatalogPath(backup);
        Assert(result.ExitCode == 0 && File.Exists(database), "a full backup writes its SQLite catalog");
        Assert(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(database), 0, 15) == "SQLite format 3", "catalog is a real SQLite file");
        CatalogData data = BackupDatabase.Read(backup);
        var sample = CurrentEntry(data, @"Samples\音 'quote'.wav");
        Assert(sample != null && sample.Size == 6 && sample.Hash == HashFile(Path.Combine(backup, sample.Path)), "Unicode and quoted names retain their path, size and expected SHA-256");
        Assert(sample.Result == "unchecked" && sample.VerifiedUtc == null, "index creation does not claim a verification was performed");
        Assert(CurrentEntry(data, "Empty").IsDirectory && data.Files.Exists(e => e.SnapshotId != "current" && !e.IsDirectory), "empty folders and snapshot contents are indexed");
        Assert(!data.Files.Exists(e => AppIdentity.IsMetadataPath(e.Path)) && !LoadChecksums(backup).ContainsKey(@".gearbackup\catalog.sqlite"), "catalog does not index or checksum itself");
        result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        data = BackupDatabase.Read(backup); sample = CurrentEntry(data, @"Samples\音 'quote'.wav");
        Assert(result.ExitCode == 0 && sample.Result == "ok" && sample.VerifiedUtc != null && sample.ObservedHash == sample.Hash, "integrity checks store the actual per-file results");
        Assert(data.Checks.Count == 1 && data.Files.Exists(e => e.SnapshotId != "current" && e.Result == "ok"), "check runs and verified snapshot references are recorded");
        string expected = sample.Hash;
        Put(Path.Combine(backup, sample.Path), "BROKEN");
        File.Delete(Path.Combine(backup, "Songs", "song.m8s"));
        Put(Path.Combine(backup, "extra.txt"), "EXTRA");
        result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        data = BackupDatabase.Read(backup); sample = CurrentEntry(data, @"Samples\音 'quote'.wav");
        Assert(result.ExitCode == 2 && sample.Result == "damaged" && sample.Hash == expected && sample.ObservedHash != expected,
            "damage detection preserves the original baseline instead of accepting damaged contents");
        Assert(CurrentEntry(data, @"Songs\song.m8s").Result == "missing" && !CurrentEntry(data, @"Songs\song.m8s").Present, "missing recorded files remain in the catalog");
        Assert(CurrentEntry(data, "extra.txt").Hash == null && CurrentEntry(data, "extra.txt").Result == "unchecked", "manual additions have no invented checksum or OK status");
        Assert(data.Checks.Count == 2, "the catalog retains completed check history");
        BackupDatabase.Rebuild(backup);
        Assert(CurrentEntry(BackupDatabase.Read(backup), sample.Path).Result == "damaged", "rebuilding preserves checks for the same expected version");
        Put(Path.Combine(source, sample.Path), "NEW CONTENT");
        result = RunBackup(source, history, backup, true, config);
        data = BackupDatabase.Read(backup);
        Assert(result.ExitCode == 0 && CurrentEntry(data, sample.Path).Result == "unchecked", "incremental updates invalidate checks for changed content");
        string indexHash = HashFile(database);
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true }.Activate()) BackupDatabase.TryRefresh(backup);
        Assert(HashFile(database) == indexHash, "preview never rewrites the catalog");
    }

    // A long history: the catalog (built with one shared name check and a staging database without its own journal)
    // must list exactly what every restore point's manifest lists.
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
        Snapshot now = ContentStoreBackup.Current(backup);
        Assert(data.Files.FindAll(e => e.SnapshotId == "current" && !e.IsDirectory).Count == now.Files.Count && now.Files.Count == 5 &&
            CurrentEntry(data, @"Songs\three.m8s") != null && CurrentEntry(data, "root.txt") == null, "the current listing matches the newest restore point");
        Assert(Directory.GetFiles(AppIdentity.MetadataRoot(backup), ".gearcatalog-*").Length == 0, "no staging file is left behind");
        BackupDatabase.Rebuild(backup);
        CatalogData rebuilt = BackupDatabase.Read(backup);
        Assert(rebuilt.Files.Count == data.Files.Count && rebuilt.RestorePoints.Count == 5, "rebuilding an existing catalog gives the same rows");

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

    static void TestBackupDatabaseSafety(string dir)
    {
        string root = Folder(dir, "sqlite-safety"), backup = Folder(root, "backup");
        string victim = Path.Combine(root, "victim.txt"), file = Path.Combine(backup, "keep.txt");
        Put(victim, "UNRELATED"); Put(file, "ORIGINAL");
        WriteChecksums(backup, null);
        BackupDatabase.Rebuild(backup);
        string database = BackupDatabase.CatalogPath(backup), sums = Path.Combine(backup, ChecksumFileName);
        byte[] baseline = File.ReadAllBytes(sums);
        string expected = CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Hash;
        Put(file, "DAMAGED"); File.WriteAllText(database, "CORRUPT DATABASE");
        BackupDatabase.Rebuild(backup);
        Assert(CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Hash == expected, "corrupt catalog rebuild uses the recorded checksum, not damaged file bytes");
        Assert(Convert.ToBase64String(File.ReadAllBytes(sums)) == Convert.ToBase64String(baseline), "database rebuild leaves checksum records unchanged");
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
        File.Delete(sums);
        BackupDatabase.Rebuild(backup);
        Assert(CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Hash == null &&
            CurrentEntry(BackupDatabase.Read(backup), "keep.txt").Result == "unchecked", "rebuild without authoritative checksums never establishes a new baseline");
        string source = Folder(root, "unchanged-source"), history = Path.Combine(root, "history");
        Put(Path.Combine(source, "song.txt"), "SONG");
        var config = new Config { SingleContentStore = false, Preview = false, PointInTimeHistory = false };
        var result = RunBackup(source, history, null, false, config);
        string clean = result.OutputFolder, catalog = BackupDatabase.CatalogPath(clean);
        Assert(File.Exists(catalog), "history-free backups also have a catalog");
        File.Delete(catalog);
        result = RunBackup(source, history, clean, true, config);
        Assert(result.ExitCode == 0 && File.Exists(catalog), "an unchanged incremental creates a missing catalog for existing backups");
        DateTime written = File.GetLastWriteTimeUtc(catalog);
        result = RunBackup(source, history, clean, true, config);
        Assert(result.ExitCode == 0 && File.GetLastWriteTimeUtc(catalog) == written, "unchanged incrementals preserve an existing catalog without redundant writes");
    }
}
