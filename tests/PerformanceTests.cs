using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

partial class Tests
{
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

    static void TestUnchangedHousekeeping(string root)
    {
        string source = Path.Combine(root, "source"), history = Path.Combine(root, "history");
        Put(Path.Combine(source, "song.txt"), "UNCHANGED");
        var config = new Config { SingleContentStore = false, Preview = false, MaxNameLength = 0 };
        OperationResult result = null;
        WithUi(() => result = Backups.RunBackup(source, history, null, false, config));
        string backup = result.OutputFolder, checksums = Path.Combine(backup, Checksums.ChecksumFileName);
        DateTime sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(checksums, sentinel);
        int reports = Directory.GetFiles(backup, "backup-statistics_*.txt").Length;
        TestInteraction ui = WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(result.ExitCode == 0 && result.Counts.Unchanged == 1 && result.Counts.Copied == 0 && ui.Plans == 0,
            "unchanged incremental finishes with correct counts without approval");
        Assert(File.GetLastWriteTimeUtc(checksums) == sentinel && Directory.GetFiles(backup, "backup-statistics_*.txt").Length == reports,
            "unchanged run preserves checksums and statistics");
        Assert(DateTime.Parse(File.ReadAllText(Path.Combine(backup, AppIdentity.MetadataFolderName, "last-success.txt"))).ToUniversalTime() > sentinel,
            "unchanged run updates dashboard success time");
        string deviceRecord = Path.Combine(backup, AppIdentity.MetadataFolderName, "device.txt");
        File.WriteAllText(deviceRecord, "unknown-device\n");
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(File.ReadAllText(deviceRecord).Trim() == Devices.M8.Id, "changed device metadata takes normal repair path");
        Directory.CreateDirectory(Path.Combine(source, "NewEmpty"));
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(Directory.Exists(Path.Combine(backup, "NewEmpty")), "new empty folders take normal backup path");
        File.SetLastWriteTimeUtc(checksums, sentinel);
        Put(Path.Combine(backup, "manual.txt"), "MANUAL");
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(Checksums.LoadChecksums(backup).ContainsKey("manual.txt"), "manual additions are still included in regenerated checksums");
        File.Delete(Path.Combine(source, "song.txt"));
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(File.Exists(Path.Combine(backup, "song.txt")) && !result.Summary.StartsWith("No new or changed"), "source deletions take normal retained-file path");
        File.Delete(checksums);
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(File.Exists(checksums), "missing checksum list is rebuilt");
        BackupStatuses.SetBackupStatus(backup, Status.Incomplete);
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(BackupStatuses.BackupStatus(backup) == Status.Complete, "unfinished backup still completes normal recovery");
        config.PointInTimeHistory = true;
        WithUi(() => result = Backups.RunBackup(source, history, backup, true, config));
        Assert(Directory.GetFiles(Snapshots.SnapshotRoot(backup), "*.manifest").Length == 1, "enabling history still writes a snapshot");
        config.PointInTimeHistory = false;
        string cleanHistory = Path.Combine(root, "clean-history");
        WithUi(() => result = Backups.RunBackup(source, cleanHistory, null, false, config));
        string cleanBackup = result.OutputFolder;
        config.KeepBackups = 1;
        WithUi(() => result = Backups.RunBackup(source, cleanHistory, cleanBackup, true, config));
        Assert(!result.Summary.StartsWith("No new or changed"), "retention uses normal housekeeping");
        config.KeepBackups = 0; config.KeepOldVersions = 1;
        WithUi(() => result = Backups.RunBackup(source, cleanHistory, cleanBackup, true, config));
        Assert(!result.Summary.StartsWith("No new or changed"), "old-version retention uses normal housekeeping");
        config.KeepOldVersions = 0; config.PointInTimeHistory = true;
        WithUi(() => result = Backups.RunBackup(source, cleanHistory, cleanBackup, true, config));
        Assert(Directory.GetFiles(Snapshots.SnapshotRoot(cleanBackup), "*.manifest").Length == 1,
            "unchanged clean backup still creates requested history");
    }

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
