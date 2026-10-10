using System;
using System.Collections.Generic;
using System.IO;

partial class GearBackup
{
    // "Check backups": verifies backup files against checksums.sha256 and history objects against their
    // manifests. Reads only; never changes a backup (apart from offering to create a missing checksum list).
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
                    return new OperationResult { ExitCode = code, Title = title, Summary = summary, Elapsed = timer.Elapsed };
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
                CheckBackup(backup, ref ok, ref damaged, ref missing, ref unchecked_, verifiedLinks);
                CheckSnapshots(backup, ref ok, ref damaged, ref missing, verifiedLinks);
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

}
