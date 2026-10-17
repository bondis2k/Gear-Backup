using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GearBackup;

partial class Tests
{
    [Test("Explicit context: prepared source device survives an operation change", Folder = "prepared-context")]
    static void TestPreparedSourceContext(string root)
    {
        string source = Folder(root, "source");
        Put(Path.Combine(source, "sample.wav"), "SAMPLE");
        var config = new Config { Device = "auto", BackupFolder = Folder(root, "backups"), SettingsDirectory = root, Preview = false };
        var prepared = Session.PrepareSource(config, new SourceChoice { Path = source, Detected = Devices.TrackerMini }, true,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        var context = new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.M8 };
        var result = Session.RunPrepared(prepared, false, false, context);
        Assert(result.Succeeded && DeviceSelection.BackupDevice(result.OutputFolder) == Devices.TrackerMini,
            "backup uses the prepared source's device even when the receiving context starts with another device");
        Assert(context.Device == Devices.M8, "backup restores the receiving context's previous device");
        string archive = Path.Combine(root, "tracker.zip");
        result = Session.RunArchive(prepared, archive, context);
        Assert(result.Succeeded && ZipRestore.Inspect(archive).Device == Devices.TrackerMini && context.Device == Devices.M8,
            "archive metadata also uses the prepared device and restores the receiving context");
    }

    [Test("Restore outcome: late cancellation preserves a failed copy", Folder = "late-restore-cancellation")]
    static void TestLateRestoreCancellation(string root)
    {
        string source = Folder(root, "source");
        Put(Path.Combine(source, "one.wav"), "ONE");
        var config = new Config { BackupFolder = root, Preview = false, Recovery = false };
        var backup = new BackupEngine().Run(source, Path.Combine(root, "history"), null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        var snapshot = ContentStore.Current(backup.OutputFolder);
        using (var cancellation = new CancellationTokenSource())
        {
            var ui = new TestInteraction { OnWriteLine = text => {
                if (text.Contains("file(s) restored") && text.Contains("failed.")) cancellation.Cancel();
            } };
            var result = new RestoreEngine().RunSelected(Folder(root, "target"), snapshot, snapshot.Files, RestoreMode.All, null, false,
                config, new OperationContext(ui, cancellation.Token), () => File.Delete(snapshot.Files[0].StoredPath));
            Assert(cancellation.IsCancellationRequested && result.Outcome == OperationOutcome.Failed && result.ExitCode == 2 &&
                result.Counts.Failed == 1, "cancellation after a copy failure does not turn its exit code into success");
        }
    }

    [Test("Explicit context: interaction, device, history and recovery isolation", Folder = "explicit-context")]
    static void TestExplicitContext(string root)
    {
        var ui = new TestInteraction();
        var context = new OperationContext(ui, CancellationToken.None);
        var otherUi = new TestInteraction();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var ambient = new OperationContext(otherUi, cancelled.Token) { PreviewOnly = true, Device = Devices.M8 };
            using (ambient.Activate())
            {
                Assert(context.ShowPlan("Explicit preview", new List<string> { "one action" }, 0, true),
                    "an explicit operation does not inherit the ambient preview flag or cancellation");
                context.WriteLine("Explicit output");
                Assert(ui.Plans == 1 && ui.Output.ToString().Contains("Explicit output") && otherUi.Plans == 0 && otherUi.Output.Length == 0,
                    "questions and output go only to the supplied interaction");
                string source = Folder(root, "source");
                var config = new Config { Device = "auto", BackupFolder = Folder(root, "backups"), SettingsDirectory = root };
                var prepared = Session.PrepareSource(config, new SourceChoice { Path = source, Detected = Devices.TrackerMini }, true, context);
                Assert(prepared.Device == Devices.TrackerMini && Path.GetFileName(prepared.History).StartsWith(Devices.TrackerMini.Id + "-") &&
                    context.Device == Devices.TrackerMini && ambient.Device == Devices.M8,
                    "source preferences and automatic history names use the supplied device without changing the ambient device");
                Histories.AvailableHistories(config, context);
                Assert(context.HistoryCache.Count == 1 && ambient.HistoryCache.Count == 0, "history cache belongs to the explicit operation");
                string missing = Path.Combine(root, "new-backups");
                Assert(Session.EnsureBackupFolder(new Config { BackupFolder = missing }, context) && Directory.Exists(missing),
                    "backup creation uses the supplied non-preview operation and its answer");
                string target = Folder(root, "target"), file = Path.Combine(target, "sample.wav");
                Put(file, "PRESERVE");
                string fingerprint = RestoreFlow.Fingerprint(file, true, context);
                var copies = new List<RestoreEntry> { new RestoreEntry { Relative = "sample.wav" } };
                var hashes = new Dictionary<string, string> { { "sample.wav", fingerprint } };
                string recovery = RestoreFlow.SaveRecovery(target, copies, new List<string>(), hashes,
                    new Config { BackupFolder = config.BackupFolder, RecoveryFolder = Folder(root, "recovery"), Recovery = true, Verify = true }, context);
                Assert(File.ReadAllText(Path.Combine(recovery, "sample.wav")) == "PRESERVE" &&
                    File.ReadAllText(Path.Combine(AppIdentity.MetadataRoot(recovery), "recovery-status.txt")).StartsWith("complete"),
                    "hashing, copying and atomic recovery records use the explicit cancellation token");
                Assert(ReferenceEquals(OperationContext.Current, ambient) && otherUi.Progress.Count == 0,
                    "explicit helpers leave ambient state and progress untouched");
            }
        }
    }

    [Test("Explicit context: nested restores do not replace engine state", Folder = "nested-restores")]
    static void TestNestedRestoreContexts(string root)
    {
        string source = Folder(root, "source");
        Put(Path.Combine(source, "one.wav"), "ONE"); Put(Path.Combine(source, "two.wav"), "TWO");
        var config = new Config { BackupFolder = root, Preview = false };
        var backup = new BackupEngine().Run(source, Path.Combine(root, "history"), null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        var snapshot = ContentStore.Current(backup.OutputFolder);
        var outerUi = new TestInteraction(); var innerUi = new TestInteraction();
        var outerContext = new OperationContext(outerUi, CancellationToken.None);
        var innerContext = new OperationContext(innerUi, CancellationToken.None);
        var engine = new RestoreEngine();
        bool nested = false;
        outerUi.OnProgress = progress => {
            if (progress.Stage != Stage.Restoring || nested) return;
            nested = true;
            var inner = new List<RestoreEntry> { snapshot.Files[0] };
            var result = engine.Run(Folder(root, "inner"), inner, new List<string>(), new List<string>(), new List<string>(),
                new Dictionary<string, string> { { inner[0].Relative, null } }, config, innerContext);
            Assert(result.Succeeded, "nested restore completes");
        };
        var targetHashes = new Dictionary<string, string>();
        foreach (var item in snapshot.Files) targetHashes.Add(item.Relative, null);
        var outer = engine.Run(Folder(root, "outer"), snapshot.Files, new List<string>(), new List<string>(), new List<string>(),
            targetHashes, config, outerContext);
        Assert(outer.Succeeded && outer.Counts.Copied == 2, "outer restore completes after a nested operation on the same engine");
        Assert(outerUi.Progress.FindAll(p => p.Stage == Stage.Restoring).Count == 2 &&
            innerUi.Progress.FindAll(p => p.Stage == Stage.Restoring).Count == 1, "each restore retains its own progress recipient");
    }
}
