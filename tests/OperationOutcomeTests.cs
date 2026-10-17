using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using GearBackup;
using GearBackup.Cli;

partial class Tests
{
    [Test("Operation outcomes: CLI follow-up requires success", Folder = "outcome-reporting")]
    static void TestOutcomeReporting(string root)
    {
        var report = typeof(Program).GetMethod("ReportResult", BindingFlags.Static | BindingFlags.NonPublic);
        var prepared = new PreparedSource { Source = new SourceChoice { PnpId = "test-device" },
            Config = new Config { Eject = EjectMode.No } };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            foreach (var outcome in new[] { OperationOutcome.Success, OperationOutcome.Cancelled, OperationOutcome.Failed, OperationOutcome.Skipped, OperationOutcome.Preview })
            {
                Notifications.FinishCalls = 0;
                var result = new OperationResult(outcome) { Title = outcome == OperationOutcome.Success ? null : "Notification" };
                report.Invoke(null, new object[] { prepared, result });
                Assert(Notifications.FinishCalls == (outcome == OperationOutcome.Success ? 1 : 0),
                    "CLI device follow-up depends on " + outcome + ", not on title presence");
            }
        }
        Notifications.FinishCalls = 0;
    }

    [Test("Operation outcomes: explicit state and exit codes", Folder = "outcomes")]
    static void TestOperationOutcomes(string root)
    {
        var success = new OperationResult(OperationOutcome.Success);
        Assert(success.Succeeded && success.Title == null && success.ExitCode == 0,
            "success does not depend on a notification title");
        foreach (var outcome in new[] { OperationOutcome.Cancelled, OperationOutcome.Skipped, OperationOutcome.Preview })
        {
            var result = new OperationResult(outcome) { Title = "A notification" };
            Assert(!result.Succeeded && result.ExitCode == 0 && result.Cancelled == (outcome == OperationOutcome.Cancelled),
                "a title does not turn " + outcome + " into success");
        }
        var failed = new OperationResult(OperationOutcome.Failed, 2);
        Assert(!failed.Succeeded && failed.ExitCode == 2, "verification failure preserves exit code 2");
        failed.SetOutcome(OperationOutcome.Cancelled);
        Assert(failed.Cancelled && failed.ExitCode == 0, "cancellation updates state and exit code together");
        Throws(() => new OperationResult(OperationOutcome.Success, 2), "success cannot carry a failing exit code");
        Throws(() => new OperationResult((OperationOutcome)999), "unknown outcomes are rejected");
    }

    [Test("Operation outcomes: engine preview, cancellation and completion", Folder = "outcome-engines")]
    static void TestEngineOutcomes(string root)
    {
        string source = Folder(root, "source"), history = Path.Combine(root, "history");
        Put(Path.Combine(source, "sample.wav"), "SAMPLE");
        var config = new Config { BackupFolder = root, Preview = true };
        var preview = new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true };
        var result = new BackupEngine().Run(source, history, null, false, config, preview);
        Assert(result.Outcome == OperationOutcome.Preview && !Directory.Exists(history), "backup preview reports preview without creating history");
        result = new BackupEngine().Run(source, history, null, false, config,
            new OperationContext(new TestInteraction { ApprovePlans = false }, CancellationToken.None));
        Assert(result.Cancelled && !Directory.Exists(history), "declining a backup plan is cancellation without writes");
        result = new BackupEngine().Run(source, history, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.Succeeded && result.Counts.Copied == 1, "a completed backup explicitly succeeds");
        var set = RestoreCatalog.Load(result.OutputFolder, null);
        var snapshot = set.Points[0].Snapshot;
        string target = Folder(root, "target");
        result = new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, RestoreMode.All, null, false, config, preview);
        Assert(result.Outcome == OperationOutcome.Preview && Directory.GetFileSystemEntries(target).Length == 0,
            "restore preview is distinct from cancellation and leaves target empty");
        result = new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, RestoreMode.All, null, false, config,
            new OperationContext(new TestInteraction { ApprovePlans = false }, CancellationToken.None));
        Assert(result.Cancelled && Directory.GetFileSystemEntries(target).Length == 0, "declining a restore plan is cancellation without writes");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            result = new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, RestoreMode.All, null, false, config,
                new OperationContext(new TestInteraction(), cancelled.Token));
            Assert(result.Outcome == OperationOutcome.Cancelled && Directory.GetFileSystemEntries(target).Length == 0,
                "cancelled restore never reports success");
        }
        result = new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, RestoreMode.All, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.Succeeded && File.Exists(Path.Combine(target, "sample.wav")), "completed restore explicitly succeeds");
        File.WriteAllText(snapshot.Files[0].StoredPath, "DAMAGED");
        result = new IntegrityEngine().Run(new List<string> { set.Backup }, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.Outcome == OperationOutcome.Failed && result.ExitCode == 2, "damaged content reports failure and preserves CLI exit code");
    }
}
