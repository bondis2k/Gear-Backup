using System;
using System.Collections.Generic;
using System.IO;
using static GearBackup.FileCopy;
using static GearBackup.PathSafety;
using static GearBackup.Snapshots;
using static GearBackup.RestoreFlow;
using static GearBackup.AtomicFile;

namespace GearBackup
{
    // A restore runs in two halves: RunSelected plans it (compares with the target, asks about differing
    // files, shows the preview), then Run carries out the approved plan.
    internal sealed class RestoreEngine
    {
        public OperationResult RunSelected(string targetRoot, Snapshot snapshot, List<RestoreEntry> items, RestoreMode mode,
            string bundle, bool exact, Config config, OperationContext context, Action beforeRestore = null, string planLabel = null)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using (context.Activate())
            {
                try
                {
                    context.CheckCancellation();
                    targetRoot = Rooted(targetRoot);
                    RejectRootLinks(targetRoot);
                    OperationResult result = new RestorePlanner(context).Run(targetRoot, snapshot, items, mode, bundle, exact, config, beforeRestore, planLabel);
                    if (context.Cancellation.IsCancellationRequested && result.Outcome != OperationOutcome.Failed) result.SetOutcome(OperationOutcome.Cancelled);
                    result.Elapsed = timer.Elapsed;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    return new OperationResult(OperationOutcome.Cancelled) { Title = AppIdentity.Name + " restore cancelled",
                        Summary = "Restore planning cancelled; target files were not changed.", Elapsed = timer.Elapsed };
                }
            }
        }

        public OperationResult Run(string targetRoot, List<RestoreEntry> copies, List<string> removals, List<string> folders,
            List<string> extraFolders, Dictionary<string, string> targetHashes, Config config, OperationContext context)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using (context.Activate())
            {
                try
                {
                    context.CheckCancellation();
                    targetRoot = Rooted(targetRoot);
                    RejectRootLinks(targetRoot);
                    OperationResult result = ExecuteCore(targetRoot, copies, removals, folders, extraFolders, targetHashes, config, context);
                    result.Elapsed = timer.Elapsed;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    return new OperationResult(OperationOutcome.Cancelled) { Title = AppIdentity.Name + " restore cancelled",
                        Summary = "Restore cancelled. Completed files remain restored; staged copies were cleaned up.", Elapsed = timer.Elapsed };
                }
            }
        }

        static void RestoreFile(string targetRoot, string target, RestoreEntry item, string expected, Config config, OperationContext context)
        {
            string temp = Path.Combine(Path.GetDirectoryName(target), ".gearrestore-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Planning already checked the content; the copy's checksum is compared below.
                ValidateEntry(item, false, context);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string hash = CopyFile(new FileInfo(item.StoredPath), temp, config.Verify, context);
                if (config.Verify && item.Hash != null && hash != item.Hash) throw new IOException("Backup checksum changed during restore.");
                DateTime modified = new DateTime(item.ModifiedTicks, DateTimeKind.Utc);
                DateTime created = new DateTime(item.CreatedTicks, DateTimeKind.Utc);
                if (UsableFileTime(modified)) File.SetLastWriteTimeUtc(temp, modified);
                if (UsableFileTime(created)) File.SetCreationTimeUtc(temp, created);
                // Copying can take time: check the current folders again before committing the staged file.
                target = SafePath(targetRoot, item.Relative);
                AssertTargetUnchanged(target, expected, context);
                context.CheckCancellation();
                if (File.Exists(target)) ReplaceSafely(temp, target);
                else File.Move(temp, target);
            }
            finally { TryDelete(temp); }
        }

        static OperationResult ExecuteCore(string targetRoot, List<RestoreEntry> copies, List<string> removals, List<string> folders,
            List<string> extraFolders, Dictionary<string, string> targetHashes, Config config, OperationContext context)
        {
            // No target mutation until every recovery copy has succeeded.
            string recovery = SaveRecovery(targetRoot, copies, removals, targetHashes, config, context);
            int copied = 0, failed = 0, removed = 0;
            foreach (string folder in folders)
            {
                context.CheckCancellation();
                Directory.CreateDirectory(SafePath(targetRoot, folder));
            }
            foreach (RestoreEntry item in copies)
            {
                context.Progress(Stage.Restoring, item.Relative, 0, 0);
                // An unsafe target aborts the whole restore rather than becoming a per-file failure.
                string target = SafePath(targetRoot, item.Relative);
                try
                {
                    RestoreFile(targetRoot, target, item, targetHashes[item.Relative], config, context);
                    copied++; context.WriteLine("  Restored: " + item.Relative);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed++; context.WriteColored("  Failed: " + item.Relative + " (" + ex.Message + ")", ConsoleColor.Red); }
            }
            // Never remove extras after a partially failed restore.
            if (failed == 0)
            {
                foreach (string relative in removals)
                {
                    context.CheckCancellation();
                    try
                    {
                        string path = SafePath(targetRoot, relative);
                        AssertTargetUnchanged(path, targetHashes[relative], context);
                        File.Delete(path);
                        removed++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failed++; context.WriteColored("  Removal failed: " + ex.Message, ConsoleColor.Red); break; }
                }
                extraFolders.Sort((a, b) => b.Length.CompareTo(a.Length));
                if (failed == 0) foreach (string relative in extraFolders)
                {
                    context.CheckCancellation();
                    string folder = SafePath(targetRoot, relative);
                    if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
                }
            }
            string summary = string.Format("{0} file(s) restored, {1} removed, {2} failed.", copied, removed, failed);
            if (recovery != null) summary += " Recovery: " + recovery;
            context.WriteLine("  " + summary);
            return new OperationResult(failed == 0 ? OperationOutcome.Success : OperationOutcome.Failed, failed == 0 ? 0 : 2) { Summary = summary,
                OutputFolder = targetRoot, Counts = new OperationCounts { Copied = copied, Removed = removed, Failed = failed },
                Title = AppIdentity.Name + (failed == 0 ? " restore complete" : " restore finished with errors") };
        }
    }

}
