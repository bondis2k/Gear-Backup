using System;
using System.IO;
using System.Linq;
using System.Threading;
using GearBackup;

partial class Tests
{
    static RestoreRequest MigrationSelection(string backup, RestorePoint point)
    { return new RestoreRequest { Backup = backup, DeviceId = Devices.M8.Id, Point = point.Id, Stamp = RestoreCatalog.Stamp(point.Snapshot) }; }

    [Test("M8 card migration: restore, verify and dependency report")]
    static void TestM8Migration(string root)
    {
        string card = Folder(root, "source-card");
        Put(Path.Combine(card, "Songs", "Demos", "relative.m8s"), "ORIGINAL SONG");
        Put(Path.Combine(card, "Songs", "Demos", "Samples", "kick.wav"), "SAMPLE");
        Directory.CreateDirectory(Path.Combine(card, "Empty"));
        var config = new Config { BackupFolder = Folder(root, "backups"), Preview = false, Verify = false };
        string history = Path.Combine(Histories.HistoriesRoot(config), "profiles", "M8");
        string backup = Backups.RunBackup(card, history, null, false, config).OutputFolder;
        var full = RestoreCatalog.Load(backup, Devices.M8).Points.First(p => p.Id != "current");
        Put(Path.Combine(card, "Songs", "Demos", "relative.m8s"), "NEWER SONG");
        Backups.RunBackup(card, history, backup, true, config);
        string target = Folder(root, "replacement");
        var request = MigrationSelection(backup, full);
        var ui = new TestInteraction { OnReview = plan => Assert(plan.Label.Contains(target) && plan.ShowDetails, "migration preview identifies the target and always shows its plan") }; M8MigrationReport report;
        using (new OperationContext(ui, CancellationToken.None).Activate()) report = M8CardMigration.Run(target, config, request, new CardTestReader());
        Assert(report.Operation.ExitCode == 0 && report.VerifiedFiles == 2 && ui.Plans == 1, "migration requires preview and verifies every restored file despite global Verify=no");
        Assert(File.ReadAllText(Path.Combine(target, "Songs", "Demos", "relative.m8s")) == "ORIGINAL SONG" && Directory.Exists(Path.Combine(target, "Empty")), "copies the selected older point and preserves empty folders");
        Assert(report.Songs.Songs.Count == 1 && report.Songs.Songs[0].Missing == 0 && !report.Songs.Songs[0].NeedsAttention, "checks restored song-relative samples against the replacement card");
        Assert(report.Text().Contains("Files verified: 2") && report.Text().Contains("relative.m8s") && !config.Verify && !config.Preview, "report identifies verification and songs without changing saved preferences");
        Throws(() => M8CardMigration.Run(target, config, request, new CardTestReader()), "refuses nonempty destination rather than replacing an existing card");
        Assert(File.ReadAllText(Path.Combine(target, "Songs", "Demos", "relative.m8s")) == "ORIGINAL SONG", "refused migration leaves existing contents alone");
        Throws(() => M8CardMigration.Run(backup, config, request, new CardTestReader()), "protects source backup storage");
        var stale = MigrationSelection(backup, full); stale.Stamp = "changed";
        Throws(() => M8CardMigration.Run(Folder(root, "stale"), config, stale, new CardTestReader()), "rejects changed point identity");
        var partial = MigrationSelection(backup, full); partial.Paths = new[] { @"Songs\Demos\relative.m8s" };
        Throws(() => M8CardMigration.Run(Folder(root, "partial"), config, partial, new CardTestReader()), "requires the complete point");
        var wrong = MigrationSelection(backup, full); wrong.DeviceId = Devices.OPXY.Id;
        Throws(() => M8CardMigration.Run(Folder(root, "wrong-device"), config, wrong, new CardTestReader()), "refuses other device types");
        string switched = Folder(root, "switched");
        ui = new TestInteraction { OnReview = plan => { Directory.Move(switched, switched + "-original"); Directory.CreateDirectory(switched); } };
        using (new OperationContext(ui, CancellationToken.None).Activate())
            Throws(() => M8CardMigration.Run(switched, config, request, new CardTestReader()), "revalidates the actual destination identity after approval");
        Assert(Directory.GetFileSystemEntries(switched).Length == 0, "destination replacement is rejected before any copy");
        using (var cancellation = new CancellationTokenSource())
        {
            string cancelled = Folder(root, "cancelled");
            ui = new TestInteraction { OnReview = plan => cancellation.Cancel() };
            using (new OperationContext(ui, cancellation.Token).Activate()) report = M8CardMigration.Run(cancelled, config, request, new CardTestReader());
            Assert(report.Operation.Cancelled && Directory.GetFileSystemEntries(cancelled).Length == 0, "cancelling at review makes no target changes");
        }
        BackupStatuses.SetBackupStatus(backup, Status.Failed);
        Throws(() => M8CardMigration.Run(Folder(root, "unfinished"), config, request, new CardTestReader()), "refuses unfinished or failed sets");
        BackupStatuses.SetBackupStatus(backup, Status.Complete);
        var objectFile = full.Snapshot.Files.First(f => f.Relative.EndsWith("kick.wav"));
        File.WriteAllText(objectFile.StoredPath, "BADBAD");
        string corruptTarget = Folder(root, "corrupt-source");
        Throws(() => M8CardMigration.Run(corruptTarget, config, request, new CardTestReader()), "mandatory source hashes detect same-size corruption before migration");
        Assert(Directory.GetFileSystemEntries(corruptTarget).Length == 0, "corrupt source backup does not produce a partially migrated card");
    }

    [Test("M8 migration reports missing song dependencies")]
    static void TestM8MigrationMissing(string root)
    {
        string card = Folder(root, "source"); Put(Path.Combine(card, "Songs", "missing.m8s"), "SONG");
        var config = new Config { BackupFolder = Folder(root, "backups"), Preview = false };
        string history = Path.Combine(Histories.HistoriesRoot(config), "profiles", "M8");
        string backup = Backups.RunBackup(card, history, null, false, config).OutputFolder;
        var point = RestoreCatalog.Load(backup, Devices.M8).Points.First(p => p.Id != "current");
        M8MigrationReport report;
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            report = M8CardMigration.Run(Folder(root, "target"), config, MigrationSelection(backup, point), new CardTestReader());
        Assert(report.VerifiedFiles == 1 && report.Operation.ExitCode != 0 && report.Songs.Songs[0].Missing == 1, "a correct copy with missing dependencies is reported as needing attention");
        Assert(report.Text().Contains("lost.wav") && report.Operation.Summary.Contains("1 with missing samples"), "report explains the unresolved dependency");
    }
}
