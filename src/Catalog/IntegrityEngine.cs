using System;
using System.Collections.Generic;
using System.IO;
using static BackupStatuses;
using static Snapshots;
using static StorageCatalog;
using static ConsoleUi;
using static PlanReview;

// "Check backups": verifies every stored content object against the checksum its restore points record.
// Records completed check results for the dashboard.
internal sealed class IntegrityEngine
{
    // Lets the user pick one backup, or all of them (the last entry), and checks them. Used by both the CLI and the
    // GUI. Title is null if nothing was checked.
    internal static OperationResult RunCheck(List<string> backups)
    {
        if (backups.Count == 0)
        {
            WriteColored("  No backups found to check.", ConsoleColor.Yellow);
            return new OperationResult { ExitCode = 1 };
        }

        WriteColored("  Check backups for damaged files", ConsoleColor.Cyan);
        var newestFirst = new List<string>(backups);
        newestFirst.Reverse();
        var labels = new List<string>();
        foreach (string backup in newestFirst) labels.Add(BackupSetNames.Label(backup) + " [" + BackupStatus(backup) + "]");
        labels.Add("All " + newestFirst.Count + " backups in this history");
        int pick = AskItem("Check which backup?", labels);
        if (pick == 0)
        {
            OperationOutput.WriteLine("  Check cancelled.");
            return new OperationResult();
        }
        List<string> toCheck = pick == labels.Count ? newestFirst : new List<string> { newestFirst[pick - 1] };
        OperationOutput.WriteLine();
        return new IntegrityEngine().Run(toCheck, OperationContext.Current);
    }

    public OperationResult Run(List<string> backups, OperationContext context)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (context.Activate())
        {
            try
            {
                context.CheckCancellation();
                string title, summary;
                int code = CheckSelected(backups, out title, out summary);
                return new OperationResult { ExitCode = code, Title = title, Summary = summary, Elapsed = timer.Elapsed,
                    OutputFolder = backups.Count == 0 ? null : backups.Count == 1 ? backups[0] : Path.GetDirectoryName(backups[0]) };
            }
            catch (OperationCanceledException)
            {
                return new OperationResult { ExitCode = 0, Cancelled = true, Title = AppIdentity.Name + " check cancelled",
                    Summary = "Integrity check cancelled; remaining files were not checked.", Elapsed = timer.Elapsed };
            }
        }
    }
    int CheckSelected(List<string> toCheck, out string title, out string summary)
    {
        int ok = 0, damaged = 0, missing = 0, unchecked_ = 0;
        foreach (string backup in toCheck)
        {
            OperationContext.Current.CheckCancellation();
            OperationOutput.WriteLine("  " + Path.GetFileName(backup));
            if (!ContentStore.IsContentStore(backup))
            {
                WriteColored("    Made in a layout older versions used, which is no longer supported; not checked.", ConsoleColor.Yellow);
                OperationOutput.WriteLine();
                unchecked_++;
                continue;
            }
            var catalogResults = new List<CatalogVerification>();
            int beforeOk = ok, beforeDamaged = damaged, beforeMissing = missing, beforeUnchecked = unchecked_;
            CheckSnapshots(backup, ref ok, ref damaged, ref missing, catalogResults);
            // The latest restore point is also the backup's "current" listing in the catalog.
            string latest = ContentStore.Current(backup).Name;
            foreach (var result in catalogResults.FindAll(r => r.SnapshotId == latest))
                catalogResults.Add(new CatalogVerification { Path = result.Path, ExpectedHash = result.ExpectedHash,
                    ObservedHash = result.ObservedHash, Result = result.Result, CheckedUtc = result.CheckedUtc });
            try { if (!PreviewOnly) SaveCheckRecord(backup, ok - beforeOk, damaged - beforeDamaged, missing - beforeMissing, unchecked_ - beforeUnchecked); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { OperationOutput.WriteLine("  Could not save check result: " + ex.Message); }
            BackupDatabase.TryRefresh(backup, catalogResults, string.Format("{0} OK, {1} damaged, {2} missing, {3} unchecked",
                ok - beforeOk, damaged - beforeDamaged, missing - beforeMissing, unchecked_ - beforeUnchecked));
        }

        summary = string.Format("{0:N0} file(s) OK", ok);
        if (damaged > 0) summary += string.Format(", {0:N0} damaged", damaged);
        if (missing > 0) summary += string.Format(", {0:N0} missing", missing);
        if (unchecked_ > 0) summary += string.Format(", {0} backup(s) not checked", unchecked_);
        summary += ".";
        bool good = damaged == 0 && missing == 0;
        WriteColored("  " + summary, good ? ConsoleColor.Green : ConsoleColor.Red);
        title = AppIdentity.Name + (good ? ": backups OK" : ": backup problems found");
        return good ? 0 : 2;
    }
}
