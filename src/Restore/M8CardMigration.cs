using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GearBackup
{
    internal sealed class M8MigrationReport
    {
        internal OperationResult Operation;
        internal string Backup, Point, Target;
        internal string HistoryLink;
        internal int VerifiedFiles;
        internal M8CardReport Songs;
        internal readonly List<string> Issues = new List<string>();

        internal string Text()
        {
            var text = new StringBuilder("M8 card migration" + Environment.NewLine);
            text.AppendLine("Backup: " + Backup); text.AppendLine("Restore point: " + Point); text.AppendLine("Target: " + Target);
            text.AppendLine(Operation.Summary); text.AppendLine("Files verified: " + VerifiedFiles);
            if (HistoryLink != null) text.AppendLine("Backup history link: " + HistoryLink);
            foreach (string issue in Issues) text.AppendLine("Verification: " + issue);
            if (Songs != null)
            {
                foreach (string warning in Songs.Warnings) text.AppendLine("Scan warning: " + warning);
                foreach (var song in Songs.Songs)
                {
                    text.AppendLine("Song: " + song.Relative + " | missing references: " + song.Missing + (song.Error == null ? "" : " | " + song.Error));
                    if (song.Samples != null)
                    {
                        foreach (var sample in song.Samples.Samples.Where(s => s.CardStatus != "Present on card"))
                            text.AppendLine("  " + sample.Reference + " | " + sample.CardStatus);
                        foreach (string warning in song.Samples.Warnings) text.AppendLine("  Warning: " + warning);
                    }
                }
            }
            else text.AppendLine("Song dependency check: not completed.");
            return text.ToString();
        }
    }

    internal static class M8CardMigration
    {
        internal static void ValidateTarget(string target, string backup, Config config, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) throw new IOException("Choose an existing, empty replacement card root or test folder.");
            PathSafety.RejectRootLinks(target);
            RestoreFlow.ValidateRestoreDestination(backup, target, config);
            bool driveRoot = PathSafety.Rooted(target) == PathSafety.Rooted(Path.GetPathRoot(Path.GetFullPath(target)));
            foreach (string entry in Directory.EnumerateFileSystemEntries(target))
            {
                context.CheckCancellation();
                string name = Path.GetFileName(entry);
                if (driveRoot && Directory.Exists(entry) && (string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))) continue;
                throw new IOException("Migration requires an empty destination. Existing item: " + entry + ". Use ordinary Restore for a card containing files.");
            }
        }

        internal static M8MigrationReport Run(string target, Config config, RestoreRequest request, IM8SongReader reader, bool linkHistory = false, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (request == null || request.DeviceId != Devices.M8.Id || request.Paths != null || request.Exact)
                throw new IOException("Card migration requires a complete M8 restore point.");
            if (!RestoreCatalog.BackupsForDevice(config, Devices.M8, context).Any(p => string.Equals(PathSafety.Rooted(p), PathSafety.Rooted(request.Backup), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("This M8 backup is no longer available. Refresh the backup list.");
            var report = new M8MigrationReport { Backup = request.Backup, Target = target };
            using (HistoryLock.LockHistory(Path.GetDirectoryName(request.Backup)))
            using (context.UseDevice(Devices.M8))
            {
                RestoreSet set = RestoreCatalog.Load(request.Backup, Devices.M8, context);
                RestorePoint point = set.Points.Find(p => p.Id == request.Point);
                if (point == null || RestoreCatalog.Stamp(point.Snapshot) != request.Stamp)
                    throw new IOException("The selected restore point changed. Refresh it before migrating.");
                if (BackupStatuses.BackupStatus(request.Backup) != Status.Complete) throw new IOException("This backup is not complete. Select a completed M8 backup for migration.");
                if (point.Snapshot.Files.Count == 0) throw new IOException("The selected backup contains no files to migrate.");
                report.Point = point.Label;
                Config effective = ProfileSettings.ResolveProfileSettings(config, Devices.M8.Id, Path.GetFileName(Path.GetDirectoryName(request.Backup)));
                effective.Verify = true; effective.Thorough = true; effective.Preview = true; effective.Recovery = false;
                ValidateTarget(target, request.Backup, effective, context);
                string history = Path.GetDirectoryName(request.Backup);
                string linkKey = linkHistory ? ProfileLinks.Key(target) : null;
                if (linkHistory) ProfileLinks.ValidateLink(config, linkKey, history);
                report.HistoryLink = linkHistory ? "Requested; not saved." : "Not requested.";
                string identity = BackupFolders.DirectoryIdentity(target);
                Action sameTarget = () => {
                    PathSafety.RejectRootLinks(target);
                    if (BackupFolders.DirectoryIdentity(target) != identity) throw new IOException("The replacement card or folder changed. Migration stopped.");
                };
                context.WriteLine("M8 migration: " + request.Backup + " · " + point.Label + " -> " + target);
                context.WriteLine("Mandatory checksum verification; no formatting or removal of existing content.");
                if (linkHistory) context.WriteLine("After successful checks, link this card to backup history: " + history);
                report.Operation = new RestoreEngine().RunSelected(target, point.Snapshot, point.Snapshot.Files, RestoreMode.All, null, false,
                    effective, context, () => { sameTarget(); ValidateTarget(target, request.Backup, effective, context); },
                    "M8 card migration to " + target + (linkHistory ? " · Link backup history: " + history : ""));
                if (!report.Operation.Succeeded) return report;
                sameTarget();
                foreach (var file in point.Snapshot.Files)
                {
                    context.CheckCancellation();
                    try
                    {
                        string path = PathSafety.SafePath(target, file.Relative);
                        Snapshots.ValidateEntry(new RestoreEntry { Relative = file.Relative, StoredPath = path, Size = file.Size, Hash = file.Hash }, true, context);
                        report.VerifiedFiles++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { report.Issues.Add(file.Relative + ": " + ex.Message); }
                }
                foreach (string folder in point.Snapshot.Folders)
                {
                    context.CheckCancellation();
                    if (!Directory.Exists(PathSafety.SafePath(target, folder))) report.Issues.Add("Missing folder: " + folder);
                }
                sameTarget();
                report.Songs = M8CardCheck.Run(target, reader, context: context);
                sameTarget();
                int missing = report.Songs.Songs.Count(s => s.Missing > 0), attention = report.Songs.Songs.Count(s => s.NeedsAttention);
                bool problem = report.Issues.Count != 0 || missing != 0 || attention != 0 || report.Songs.Warnings.Count != 0;
                report.Operation.SetOutcome(problem ? OperationOutcome.Failed : OperationOutcome.Success, problem ? 2 : 0);
                report.Operation.Title = problem ? "M8 migration needs attention" : "M8 migration complete";
                report.Operation.Summary = report.VerifiedFiles + " / " + point.Snapshot.Files.Count + " file(s) verified; " + report.Songs.Songs.Count +
                    " song(s) checked; " + missing + " with missing samples; " + attention + " needing attention; " + report.Issues.Count + " verification issue(s)." +
                    (report.Songs.Warnings.Count == 0 ? "" : " Song scan incomplete; see report.");
                if (linkHistory && !problem)
                {
                    context.CheckCancellation(); sameTarget();
                    try
                    {
                        if (ProfileLinks.Key(target) != linkKey) throw new IOException("The replacement card volume ID changed.");
                        ProfileLinks.Save(config, linkKey, history);
                        report.HistoryLink = "Linked to " + history;
                        report.Operation.Summary += " Replacement card linked to the original backup history.";
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        report.HistoryLink = "Not saved: " + ex.Message;
                        report.Operation.SetOutcome(OperationOutcome.Failed, 2); report.Operation.Title = "M8 migration needs attention";
                        report.Operation.Summary += " Files migrated, but the history link could not be saved. See report.";
                    }
                }
                context.WriteLine(report.Text());
                report.Operation.Elapsed = timer.Elapsed;
                return report;
            }
        }
    }
}
