using System;
using System.Collections.Generic;
using System.IO;

partial class M8Backup
{
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
                    return new OperationResult { ExitCode = 0, Cancelled = true, Title = "M8 check cancelled",
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
                CheckBackup(backup, ref ok, ref damaged, ref missing, ref unchecked_);
                CheckSnapshots(backup, ref ok, ref damaged, ref missing);
            }

            summary = string.Format("{0:N0} file(s) OK", ok);
            if (damaged > 0) summary += string.Format(", {0:N0} damaged", damaged);
            if (missing > 0) summary += string.Format(", {0:N0} missing", missing);
            if (unchecked_ > 0) summary += string.Format(", {0} backup(s) without a checksum list", unchecked_);
            summary += ".";
            bool good = damaged == 0 && missing == 0;
            WriteColored("  " + summary, good ? ConsoleColor.Green : ConsoleColor.Red);
            title = good ? "M8 backups OK" : "M8 backup problems found";
            return good ? 0 : 2;
        }
    }

}
