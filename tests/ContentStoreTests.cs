using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using static Backups;
using static Snapshots;
using static FileCopy;
using static Session;

partial class Tests
{
    static void TestSingleContentStore(string dir)
    {
        string root = Folder(dir, "single-store"), source = Folder(root, "source");
        Put(Path.Combine(source, "Songs", "one.txt"), "SAME");
        Put(Path.Combine(source, "Samples", "two.wav"), "SAME");
        Put(Path.Combine(source, "keep.txt"), "KEEP");
        DateTime oldTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, "Songs", "one.txt"), oldTime);
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        string history = Path.Combine(HistoriesRoot(new Config { SingleContentStore = false, BackupFolder = root }), "profiles", "Single");
        var config = new Config { SingleContentStore = true, Preview = false, PointInTimeHistory = false, HardLinks = false, Verify = true };
        var result = RunBackup(source, history, null, false, config);
        string backup = result.OutputFolder;
        Assert(result.ExitCode == 0 && ContentStoreBackup.IsSingle(backup), "full backup creates a single-store set without enabling copied history");
        string objects = Path.Combine(AppIdentity.MetadataRoot(backup), "objects");
        Assert(Directory.GetFiles(objects).Length == 2, "identical source files share one content object without hard links");
        long bytes = 0; foreach (string obj in Directory.GetFiles(objects)) bytes += new FileInfo(obj).Length;
        Assert(bytes == 8 && !Directory.Exists(Path.Combine(backup, "Songs")) && !File.Exists(Path.Combine(backup, "keep.txt")), "content is stored once with no duplicate browsable tree");
        Snapshot first = ContentStoreBackup.Current(backup);
        Assert(first.Files.Count == 3 && first.Folders.Contains("Empty"), "restore point records every file and empty folder");
        Assert(first.Name.StartsWith(DateTime.Now.ToString("yyyy-MM-dd")) && first.Files.Find(e => e.Relative == @"Songs\one.txt").ModifiedTicks == oldTime.Ticks,
            "restore point uses backup-run time while retaining the file's original date");
        Assert(BackupDatabase.Read(backup).Files.Exists(e => e.SnapshotId == "current" && e.Path == @"Songs\one.txt"), "SQLite indexes the logical current files");

        Put(Path.Combine(source, "Songs", "one.txt"), "UPDATED");
        File.Delete(Path.Combine(source, "Samples", "two.wav"));
        Directory.Delete(Path.Combine(source, "Empty"));
        Directory.CreateDirectory(Path.Combine(source, "New empty"));
        result = RunBackup(source, history, backup, true, config);
        Snapshot second = ContentStoreBackup.Current(backup);
        Assert(result.ExitCode == 0 && second.Name != first.Name && Directory.GetFiles(objects).Length == 3, "incremental adds only changed content and creates a new dated point");
        Assert(second.Files.Count == 2 && !second.Folders.Contains("Empty") && second.Folders.Contains("New empty"), "new point records deletions and folder membership");
        Snapshot savedFirst = ReadSnapshot(backup, Path.Combine(SnapshotRoot(backup), first.Name + ".manifest"));
        string target = Folder(root, "restored-first");
        result = new RestoreEngine().RunSelected(target, savedFirst, savedFirst.Files, RestoreMode.All, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.txt")) == "SAME" && File.Exists(Path.Combine(target, "Samples", "two.wav")), "earlier point restores changed and subsequently deleted files");
        Assert(File.GetLastWriteTimeUtc(Path.Combine(target, "Songs", "one.txt")) == oldTime, "restore preserves original file dates independently of point dates");
        target = Folder(root, "restored-latest");
        result = new RestoreEngine().RunSelected(target, CurrentBackup(backup), second.Files, RestoreMode.All, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.txt")) == "UPDATED" && !File.Exists(Path.Combine(target, "Samples", "two.wav")), "current-file restore resolves through the latest content-store point");
        int points = Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length;
        var ui = new TestInteraction();
        using (new OperationContext(ui, CancellationToken.None).Activate()) result = RunBackup(source, history, backup, true, config);
        Assert(result.ExitCode == 0 && ui.Plans == 0 && Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == points + 1 && Directory.GetFiles(objects).Length == 3,
            "unchanged incremental creates a restore point without copying content or asking to proceed");
        result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && BackupDatabase.Read(backup).Files.Exists(e => e.SnapshotId == "current" && e.Result == "ok"), "integrity checks verify stored objects and update logical current-file results");
        var storage = StorageCatalog.ScanBackupCatalog(new Config { SingleContentStore = false, BackupFolder = root }, Path.Combine(root, "no-mirrors"));
        Assert(storage.Warnings.Count == 0 && storage.Backups[0].FilesBytes == 0 && storage.Backups[0].ObjectsBytes == 15, "Storage counts physical content once, not logical snapshot copies");
        string archive = BackupZip.ZipBackup(backup);
        using (var zip = new ZipArchive(File.OpenRead(archive), ZipArchiveMode.Read))
        {
            Assert(zip.GetEntry("Songs/one.txt") != null && zip.GetEntry("checksums.sha256") != null && zip.GetEntry("New empty/") != null,
                "ZIP exports ordinary current files, checksums and empty folders directly from the store");
            using (var reader = new StreamReader(zip.GetEntry("Songs/one.txt").Open())) Assert(reader.ReadToEnd() == "UPDATED", "export reconstructs latest file content");
        }
        File.WriteAllText(BackupDatabase.CatalogPath(backup), "CORRUPT");
        target = Folder(root, "restored-without-database");
        result = new RestoreEngine().RunSelected(target, CurrentBackup(backup), second.Files, RestoreMode.All, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.txt")) == "UPDATED", "restore does not depend on a usable SQLite catalog");
        BackupDatabase.Rebuild(backup);
        Assert(CurrentBackup(backup).Files.Count == 2 && BackupDatabase.Read(backup).Files.Count > 2, "catalog corruption is recoverable from manifests without losing points");
        string objectPath = second.Files.Find(e => e.Relative == @"Songs\one.txt").StoredPath;
        Put(objectPath, "BROKEN!");
        result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 2 && BackupDatabase.Read(backup).Files.Exists(e => e.SnapshotId == "current" && e.Result == "damaged"), "stored corruption is detected across restore points");
    }

    static void TestSingleContentStoreSafety(string dir)
    {
        Assert(new Config().SingleContentStore, "new full backups use a single content store by default");
        {
            string checkRoot = Folder(dir, "object-checks"), good = new string('a', 64);
            var objects = new ObjectChecks(checkRoot);
            Assert(!objects.Present(good) && objects.PathOf(good).EndsWith(good), "object checks name a stored object by its checksum alone");
            Throws(() => objects.PathOf(@"..\outside"), "object checks refuse a name that is not a checksum");
            Throws(() => objects.PathOf(new string('A', 64)), "object checks accept lower-case hexadecimal checksums only");
        }
        string root = Folder(dir, "single-safety"), source = Folder(root, "source"), history = Path.Combine(root, "history");
        Put(Path.Combine(source, "one.txt"), "ONE"); Put(Path.Combine(source, "two.txt"), "TWO");
        var config = new Config { SingleContentStore = true, Preview = false };
        var result = RunBackup(source, history, null, false, config); string backup = result.OutputFolder;
        string prior = ContentStoreBackup.Current(backup).Name;
        Put(Path.Combine(source, "one.txt"), "NEW ONE"); Put(Path.Combine(source, "two.txt"), "NEW TWO");
        var cancel = new CancellationTokenSource(); var ui = new TestInteraction(); int callbacks = 0;
        ui.OnProgress = p => { if (p.Stage == Stage.Copying && ++callbacks == 2) cancel.Cancel(); };
        result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(ui, cancel.Token));
        Assert(result.Cancelled && ContentStoreBackup.Current(backup).Name == prior && Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == 1,
            "cancelled capture never publishes an incomplete point or replaces the current pointer");
        Assert(Directory.GetFiles(Path.Combine(AppIdentity.MetadataRoot(backup), "objects"), "*.tmp").Length == 0, "interrupted copying cleans its staging file");
        result = RunBackup(source, history, backup, true, config);
        Assert(result.ExitCode == 0 && ContentStoreBackup.Current(backup).Name != prior, "interrupted single-store backup resumes safely");
        string original = ContentStoreBackup.Current(backup).Name;
        ui = new TestInteraction(); ui.OnReview = p => Put(Path.Combine(source, "one.txt"), "MUTATED DURING PREVIEW");
        Put(Path.Combine(source, "one.txt"), "PLANNED CONTENT"); config.Preview = true;
        result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(ui, CancellationToken.None));
        Assert(result.ExitCode == 2 && ContentStoreBackup.Current(backup).Name == original, "source changed during review cannot publish a new point");
        config.Preview = false;
        int count = Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length;
        result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true });
        Assert(result.Title == null && Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == count, "preview-only single-store backup is read-only");
        result = RunBackup(source, history, backup, true, new Config { Preview = false, SingleContentStore = false });
        Assert(result.ExitCode == 0 && !File.Exists(Path.Combine(backup, "one.txt")), "an existing single-store set keeps its layout when preferences change");
        string pointer = Path.Combine(AppIdentity.MetadataRoot(backup), "current-snapshot.txt");
        original = ContentStoreBackup.Current(backup).Name;
        count = Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length;
        Put(Path.Combine(source, "added.txt"), "NEW FILE");
        using (var locked = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = RunBackup(source, history, backup, true, config);
        Assert(result.ExitCode == 2 && ContentStoreBackup.Current(backup).Name == original && Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == count,
            "failed current-pointer publication preserves the old head and removes the unpublished manifest");
        string pointerText = File.ReadAllText(pointer);
        Put(pointer, "..\\outside");
        Throws(() => ContentStoreBackup.Current(backup), "current pointer cannot escape the snapshot folder");
        Put(pointer, pointerText);
        var current = ContentStoreBackup.Current(backup);
        string storedPath = current.Files[0].StoredPath, victim = Path.Combine(root, "outside.txt");
        Put(victim, "OUTSIDE");
        File.Move(storedPath, storedPath + ".saved");
        CreateTestFileSymlink(storedPath, victim);
        try { Throws(() => ContentStoreBackup.Current(backup), "single store rejects a linked content object"); }
        finally { File.Delete(storedPath); File.Move(storedPath + ".saved", storedPath); }
        Assert(File.ReadAllText(victim) == "OUTSIDE", "linked-object rejection leaves external content untouched");
    }
}
