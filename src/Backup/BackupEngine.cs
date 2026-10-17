using System;
using System.IO;
using static GearBackup.PathSafety;

namespace GearBackup
{
    // Starting a backup: validates the folders involved, then hands over to the content store, which saves a restore point.
    internal sealed class BackupEngine
    {
        public OperationResult Run(string source, string backupRoot, string previous, bool incremental, Config config, OperationContext context)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool previousProgressMode = context.OverallProgressOnly;
            using (context.Activate())
            {
                try
                {
                    context.OverallProgressOnly = !incremental;
                    context.CheckCancellation();
                    BackupSetNames.Validate(incremental ? config.IncrementalBackupName : config.FullBackupName);
                    source = Rooted(source);
                    backupRoot = Path.GetFullPath(backupRoot);
                    if (!Directory.Exists(source)) throw new IOException("Source folder does not exist: " + source);
                    RejectRootLinks(source);
                    RejectRootLinks(backupRoot);
                    if (Within(source, backupRoot) || Within(backupRoot, source))
                        throw new IOException("The backup folder and source must not contain one another.");
                    if (previous != null)
                    {
                        previous = Path.GetFullPath(previous);
                        if (!Within(previous, backupRoot) || Rooted(previous) == Rooted(backupRoot))
                            throw new IOException("The previous backup must belong to the selected history.");
                        RejectRootLinks(previous);
                    }
                    if (incremental && (previous == null || !Directory.Exists(previous)))
                        throw new IOException("An incremental backup needs an existing backup folder.");
                    // An incremental backup updates the backup it names, which must be a content store.
                    if (incremental) ContentStore.RequireContentStore(previous);
                    return new ContentStoreBackup(context).Run(source, backupRoot, previous, incremental, config);
                }
                catch (OperationCanceledException)
                {
                    return new OperationResult(OperationOutcome.Cancelled) { Title = AppIdentity.Name + " cancelled",
                        Summary = "Backup cancelled. Completed copies remain available; incomplete backups can be resumed.", Elapsed = timer.Elapsed };
                }
                finally { context.OverallProgressOnly = previousProgressMode; }
            }
        }
    }

}
