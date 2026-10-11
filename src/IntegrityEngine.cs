using System;
using System.Collections.Generic;
using System.IO;
using static Checksums;
using static Snapshots;
using static StorageCatalog;
using static ConsoleUi;
using static Workflow;

// "Check backups": verifies backup files against checksums.sha256 and history objects against their
// manifests. Records completed check results for the dashboard.
internal sealed class IntegrityEngine
{
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
            var verifiedLinks = new Dictionary<string, string>();
            bool hadChecksums = File.Exists(Path.Combine(backup, ChecksumFileName));
            int beforeOk = ok, beforeDamaged = damaged, beforeMissing = missing, beforeUnchecked = unchecked_;
            CheckBackup(backup, ref ok, ref damaged, ref missing, ref unchecked_, verifiedLinks);
            CheckSnapshots(backup, ref ok, ref damaged, ref missing, verifiedLinks);
            try { if (!PreviewOnly) SaveCheckRecord(backup, ok - beforeOk, damaged - beforeDamaged, missing - beforeMissing, Math.Max(unchecked_ - beforeUnchecked, hadChecksums ? 0 : 1)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { OperationOutput.WriteLine("  Could not save check result: " + ex.Message); }
        }

        summary = string.Format("{0:N0} file(s) OK", ok);
        if (damaged > 0) summary += string.Format(", {0:N0} damaged", damaged);
        if (missing > 0) summary += string.Format(", {0:N0} missing", missing);
        if (unchecked_ > 0) summary += string.Format(", {0} backup(s) without a checksum list", unchecked_);
        summary += ".";
        bool good = damaged == 0 && missing == 0;
        WriteColored("  " + summary, good ? ConsoleColor.Green : ConsoleColor.Red);
        title = AppIdentity.Name + (good ? ": backups OK" : ": backup problems found");
        return good ? 0 : 2;
    }
}
