using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

partial class Tests
{
    [Test("Dashboard metadata scan", Folder = "dashboard-metadata")]
    static void TestDashboardMetadata(string root)
    {
        Config config;
        BackupCatalog full = ManagementFixture(root, out config);
        BackupCatalog light = null;
        TestInteraction ui = WithUi(() => light = StorageCatalog.ScanDashboardCatalog(config));
        Assert(!light.StorageDetails && light.Cleanup.Count == 0 && light.Counted.Count == 0,
            "Dashboard does not calculate storage identities or cleanup");
        Assert(light.Backups.Count == full.Backups.Count, "Dashboard retains all backup rows");
        foreach (CatalogBackup row in full.Backups)
        {
            CatalogBackup metadata = light.Backups.Find(b => b.Path == row.Path);
            Assert(metadata.Status == row.Status && metadata.CompletedUtc == row.CompletedUtc &&
                metadata.CheckedUtc == row.CheckedUtc && metadata.CheckResult == row.CheckResult &&
                metadata.Device == row.Device && metadata.Latest == row.Latest, "Dashboard health matches full scan");
        }
        Assert(!ui.Progress.Exists(p => p.Stage == Stage.ScanningBackup), "Dashboard never walks backup contents");
        var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool stopped = false;
        using (new OperationContext(new TestInteraction(), cancel.Token).Activate())
            try { StorageCatalog.ScanDashboardCatalog(config); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped, "metadata scan remains cancellable");
    }

    [Test("Unchanged incremental housekeeping", Folder = "unchanged-housekeeping")]
    static void TestUnchangedHousekeeping(string root)
    {
        string source = Path.Combine(root, "source"), history = Path.Combine(root, "history");
        Put(Path.Combine(source, "song.txt"), "UNCHANGED");
        var config = new Config { Preview = false, MaxNameLength = 0 };
        OperationResult result = null;
        WithUi(() => result = Backups.RunBackup(source, history, null, false, config));
        string backup = result.OutputFolder;
        DateTime sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string success = Path.Combine(backup, AppIdentity.MetadataFolderName, "last-success.txt");
        File.WriteAllText(success, sentinel.ToString("O") + "\n");
        TestInteraction ui = WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(result.ExitCode == 0 && result.Counts.Unchanged == 1 && result.Counts.Copied == 0 && ui.Plans == 0,
            "unchanged incremental finishes with correct counts without approval");
        Assert(DateTime.Parse(File.ReadAllText(success)).ToUniversalTime() > sentinel, "unchanged run updates dashboard success time");
        string deviceRecord = Path.Combine(backup, AppIdentity.MetadataFolderName, "device.txt");
        File.WriteAllText(deviceRecord, "unknown-device\n");
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(File.ReadAllText(deviceRecord).Trim() == Devices.M8.Id, "changed device metadata is repaired by the next run");
        Directory.CreateDirectory(Path.Combine(source, "NewEmpty"));
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(Snapshots.CurrentBackup(backup).Folders.Contains("NewEmpty"), "new empty folders are recorded in the next restore point");
        File.Delete(Path.Combine(source, "song.txt"));
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(Snapshots.CurrentBackup(backup).Files.Count == 0 && !result.Summary.StartsWith("No new or changed"), "source deletions are recorded without touching earlier restore points");
        Assert(Directory.GetFiles(Snapshots.SnapshotRoot(backup), "*.manifest").Length == 5, "every run saves a restore point");
        BackupStatuses.SetBackupStatus(backup, Status.Incomplete);
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(BackupStatuses.BackupStatus(backup) == Status.Complete, "an unfinished backup still completes");
        string cleanHistory = Path.Combine(root, "clean-history");
        Put(Path.Combine(source, "song.txt"), "UNCHANGED");
        WithUi(() => result = Backups.RunBackup(source, cleanHistory, null, false, config));
        string cleanBackup = result.OutputFolder;
        config.KeepBackups = 1;
        WithUi(() => result = Backups.RunBackup(source, cleanHistory, cleanBackup, true, config));
        Assert(Directory.GetFiles(Snapshots.SnapshotRoot(cleanBackup), "*.manifest").Length == 2, "an unchanged run still saves its restore point with retention on");
    }

    [Test("Reusable copy and hash buffers", Folder = "reusable-buffers")]
    static void TestReusableBuffers(string root)
    {
        var ui = new TestInteraction();
        bool nested = false;
        byte[] other = new byte[] { 9, 8, 7 };
        ui.OnProgress = p => {
            if (nested || p.Stage != Stage.Copying) return;
            nested = true;
            using (var input = new MemoryStream(other)) using (var output = new MemoryStream())
            {
                FileCopy.CopyAndHash(input, output, "nested");
                Assert(output.ToArray().SequenceEqual(other), "nested copy has independent buffers");
            }
        };
        using (new OperationContext(ui, CancellationToken.None).Activate())
        {
            var random = new Random(31);
            foreach (int size in new[] { 0, 1, 65535, 65536, 65537, FileCopy.ChunkSize + 1, 4096 })
            {
                byte[] data = new byte[size]; random.NextBytes(data);
                string expected;
                using (var sha = SHA256.Create()) expected = FileCopy.ToHex(sha.ComputeHash(data));
                string path = Path.Combine(root, size + ".bin"); File.WriteAllBytes(path, data);
                using (var input = new MemoryStream(data)) using (var output = new MemoryStream())
                    Assert(FileCopy.CopyAndHash(input, output, path) == expected && output.ToArray().SequenceEqual(data), "reused copy buffers at " + size);
                Assert(FileCopy.HashFile(path) == expected && FileCopy.HashFileFromDisk(path) == expected, "reused hashing buffers at " + size);
            }
        }
        Assert(nested, "nested buffer regression exercised");
    }
}
