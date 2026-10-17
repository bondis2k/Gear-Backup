using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static GearBackup.FileCopy;
using static GearBackup.PathSafety;
using static GearBackup.Snapshots;
using static GearBackup.Backups;
using static GearBackup.ChangeSummary;
using static GearBackup.SampleDependencies;
using static GearBackup.ConsoleUi;
using static GearBackup.Formatting;
using static GearBackup.DeviceSelection;
using static GearBackup.ProfileSettings;
using static GearBackup.AtomicFile;
using static GearBackup.BackupStatuses;
using static GearBackup.Histories;
using static GearBackup.SongRestoreDependencies;

namespace GearBackup
{
    static class RestoreFlow
    {
        // The outcome distinguishes a completed restore from a cancelled or empty selection.
        internal static OperationResult RunRestore(string m8, List<string> backups, Config config, Config global = null, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            if (backups.Count == 0) { context.WriteLine("  No backups found."); return new OperationResult(OperationOutcome.Failed, 1); }
            var choices = new List<string>(backups);
            choices.Reverse();
            var labels = new List<string>();
            foreach (string choice in choices) labels.Add(BackupSetNames.Label(choice) + " [" + BackupStatus(choice) + "]");
            int pick = context.AskItem("Restore from which backup?", labels);
            if (pick == 0) return new OperationResult(OperationOutcome.Cancelled);
            string backup = choices[pick - 1];
            // The backup's own device decides the restore choices and preferences; the source's device
            // comes back afterwards.
            DeviceProfile device = BackupDevice(backup);
            if (device == null) throw new IOException("Backup device metadata is missing or invalid. Select a valid backup.");
            using (context.UseDevice(device))
            {
                if (global != null)
                    config = ResolveProfileSettings(global, context.Device.Id, Path.GetFileName(Path.GetDirectoryName(backup)));
                return RestoreSelectedBackup(m8, backup, config, context);
            }
        }

        static OperationResult RestoreSelectedBackup(string m8, string backup, Config config, OperationContext context)
        {
            ValidateRestoreDestination(backup, m8, config);
            Snapshot snapshot = ChooseSnapshot(backup, context);
            bool historical = snapshot != null;
            if (!historical)
            {
                Status status = BackupStatus(backup);
                if (status != Status.Complete && !context.AskYesNo("Backup status is " + status + ". Browse its available files anyway?")) return new OperationResult(OperationOutcome.Cancelled);
                snapshot = CurrentBackup(backup);
            }
            context.WriteLine("  Selected: " + snapshot.Name);
            RestoreMode mode = RestoreMode.FromKey(context.Ask("What should be restored?", RestoreModes(context.Device)));
            List<RestoreEntry> items;
            string bundle = null;
            if (mode == RestoreMode.All) items = new List<RestoreEntry>(snapshot.Files);
            else if (mode == RestoreMode.Bundle || mode == RestoreMode.ProjectFolder)
            {
                // A bundle or a song folder: everything under one folder, restored like a bundle.
                bundle = mode == RestoreMode.Bundle ? PickBundle(snapshot, context) : PickSongFolder(snapshot, context);
                if (bundle == null) return new OperationResult(OperationOutcome.Cancelled);
                items = snapshot.Files.FindAll(f => f.Relative.StartsWith(bundle + "\\", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                RestoreEntry file = PickEntry(snapshot, mode == RestoreMode.SongWithSamples, context);
                if (file == null) return new OperationResult(OperationOutcome.Cancelled);
                items = mode == RestoreMode.SongWithSamples ? SongDependencies(file, snapshot, m8, config.Verify, context) : new List<RestoreEntry> { file };
            }
            if (items.Count == 0) { context.WriteLine("  No files selected."); return new OperationResult(OperationOutcome.Skipped); }

            if (!ConfirmRestoreTarget(backup, m8, context)) return new OperationResult(OperationOutcome.Cancelled);

            // Exact restore is available only for manifests, whose file membership is authoritative.
            bool exact = historical && mode == RestoreMode.All && context.Ask("How should the snapshot be restored?", new Option('m', "Merge"),
                new Option('e', "Exact snapshot (remove extra files)")) == 'e';
            return new RestoreEngine().RunSelected(m8, snapshot, items, mode, bundle, exact, config, context,
                () => ValidateRestoreDestination(backup, m8, config));
        }

        internal static void ValidateRestoreDestination(string backup, string target, Config config = null)
        {
            RejectRootLinks(backup); RejectRootLinks(target);
            if (Within(target, backup) || Within(backup, target))
                throw new IOException("The restore destination and backup must not contain one another.");
            if (config != null)
            {
                foreach (string protectedRoot in new[] { Path.Combine(config.BackupFolder, AppIdentity.BackupsFolderName), RecoveryRoot(config) })
                    if (Within(target, protectedRoot) || Within(protectedRoot, target))
                        throw new IOException("Restore outside the backup repository and recovery storage: " + protectedRoot);
            }
        }

        // Returns the exit code; title/summary are for the final notification (null if nothing was done).
        internal static int Restore(string m8, List<string> backups, Config config, out string title, out string summary)
        {
            var context = OperationContext.Current;
            OperationResult result = RunRestore(m8, backups, config, context: context);
            title = result.Title;
            summary = result.Summary;
            return result.ExitCode;
        }

        // Throws if the backup copy is missing, has the wrong size, or (with verify) a different checksum
        // than when it was backed up.
        // A camera's photos going back onto a memory card (a drive root) are almost always a mistake, so this
        // explains why and asks first. True to go ahead.
        internal static bool ConfirmRestoreTarget(string backup, string target, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            DeviceProfile from = BackupDevice(backup);
            if (from == null || from.RestoreWarning == null || Rooted(target) != Rooted(Path.GetPathRoot(Rooted(target)))) return true;
            context.WriteLine("  " + target + " is a memory card. " + from.RestoreWarning);
            return context.AskYesNo("Restore the " + from.Name + "'s files onto " + target + " anyway?");
        }

        // The restore choices a device supports: bundles and song-with-samples only where it has them.
        internal static Option[] RestoreModes(DeviceProfile device)
        {
            var modes = new List<Option> { new Option(RestoreMode.All.Key, "All files") };
            if (device.BundlesFolder != null) modes.Add(new Option(RestoreMode.Bundle.Key, "Bundle"));
            if (device.SongIsFolder) modes.Add(new Option(RestoreMode.ProjectFolder.Key, "Whole " + Singular(device.SongsLabel) + " folder"));
            modes.Add(new Option(RestoreMode.SingleFile.Key, "Single file"));
            if (device.ReadsSamplePaths) modes.Add(new Option(RestoreMode.SongWithSamples.Key, "Song with dependent samples"));
            return modes.ToArray();
        }

        internal static bool EntryMatchesTarget(RestoreEntry item, string target, bool thorough)
        {
            return EntryMatchesTarget(item, target, thorough, null);
        }

        // Whether the file on the target already equals the backup's, so restoring it can be skipped.
        // plannedFingerprint lets a checksum taken during planning be reused instead of reading the file again.
        internal static bool EntryMatchesTarget(RestoreEntry item, string target, bool thorough, string plannedFingerprint, OperationContext context = null)
        {
            var info = new FileInfo(target);
            if (!info.Exists || info.Length != item.Size) return false;
            if (thorough)
            {
                string targetHash = plannedFingerprint != null && plannedFingerprint.StartsWith("sha:", StringComparison.Ordinal)
                    ? plannedFingerprint.Substring(4) : HashFile(target, context);
                return targetHash == (item.Hash ?? HashFile(item.StoredPath, context));
            }
            if (!UsableFileTime(new DateTime(item.ModifiedTicks, DateTimeKind.Utc)) || !UsableFileTime(info.LastWriteTimeUtc))
                return FilesEqual(item.StoredPath, target, context);
            return Math.Abs(info.LastWriteTimeUtc.Ticks - item.ModifiedTicks) <= TimeSpan.FromSeconds(2).Ticks;
        }

        // A short description of a target file's state, to notice if it changes between the preview and the
        // copy: "sha:<checksum>" or "meta:<size>:<modified ticks>". Null if the file doesn't exist.
        internal static string Fingerprint(string path, bool checksum, OperationContext context = null)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return checksum ? "sha:" + HashFile(path, context) : "meta:" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }

        // Space needed on the target: new files, growth of replaced files, and room for the largest replaced
        // file, because each replacement briefly exists twice (new copy written before the old one goes).
        internal static long RestoreSpaceNeeded(string targetRoot, List<RestoreEntry> copies)
        {
            long needed = 0, largest = 0;
            foreach (RestoreEntry item in copies)
            {
                var existing = new FileInfo(SafePath(targetRoot, item.Relative));
                if (!existing.Exists) needed += item.Size;
                else { needed += Math.Max(0, item.Size - existing.Length); largest = Math.Max(largest, item.Size); }
            }
            return needed + largest;
        }

        internal static string RecoveryRoot(Config config)
        {
            return config.RecoveryFolder.Length > 0 ? config.RecoveryFolder : Path.Combine(config.BackupFolder, AppIdentity.RecoveryFolderName);
        }

        internal static void AssertTargetUnchanged(string target, string expected, OperationContext context = null)
        {
            string current = Fingerprint(target, expected != null && expected.StartsWith("sha:", StringComparison.Ordinal), context);
            if (current != expected) throw new IOException("Target changed since the preview: " + target);
        }

        // Recovery=yes: copies every target file the restore will replace or remove to a dated folder on the
        // PC first. Returns that folder, or null when there's nothing to save. Any failure here stops the
        // restore before the target is touched.
        internal static string SaveRecovery(string targetRoot, List<RestoreEntry> copies, List<string> removals,
            Dictionary<string, string> targetHashes, Config config, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            if (!config.Recovery) return null;
            var paths = new HashSet<string>(removals, StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry item in copies) if (targetHashes[item.Relative] != null) paths.Add(item.Relative);
            if (paths.Count == 0) return null;
            string root = RecoveryRoot(config);
            RejectRootLinks(root);
            if (Within(root, targetRoot) || Within(targetRoot, root)) throw new IOException("Recovery folder and target must not contain one another.");
            string folder = UniqueFolder(root, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            long needed = 0;
            foreach (string relative in paths) needed += new FileInfo(SafePath(targetRoot, relative)).Length;
            string space = CheckFreeSpace(root, needed);
            if (space != null) throw new IOException(space);
            Directory.CreateDirectory(folder);
            AtomicText(Path.Combine(AppIdentity.MetadataRoot(folder), "recovery-status.txt"), "incomplete\n", context);
            var sums = new StringBuilder();
            foreach (string relative in paths)
            {
                context.Progress(Stage.SavingRecovery, relative, 0, 0);
                string from = SafePath(targetRoot, relative), to = SafePath(folder, relative);
                AssertTargetUnchanged(from, targetHashes[relative], context);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                string hash = CopyFile(new FileInfo(from), to, config.Verify, context);
                AssertTargetUnchanged(from, targetHashes[relative], context);
                if (config.Verify && targetHashes[relative].StartsWith("sha:", StringComparison.Ordinal) &&
                    "sha:" + hash != targetHashes[relative]) throw new IOException("Recovery source changed: " + relative);
                sums.Append(hash).Append("  ").Append(relative.Replace('\\', '/')).Append('\n');
            }
            AtomicText(Path.Combine(AppIdentity.MetadataRoot(folder), "recovery-checksums.sha256"), sums.ToString(), context);
            AtomicText(Path.Combine(AppIdentity.MetadataRoot(folder), "recovery-status.txt"), "complete\nOriginal target: " + targetRoot + "\n", context);
            context.WriteLine("  Recovery copies saved to " + folder);
            return folder;
        }

        internal static void ExecuteRestore(string targetRoot, List<RestoreEntry> copies, List<string> removals, List<string> folders,
            List<string> extraFolders, Dictionary<string, string> targetHashes, Config config, out string title, out string summary)
        {
            var context = OperationContext.Current;
            OperationResult result = new RestoreEngine().Run(targetRoot, copies, removals, folders, extraFolders, targetHashes, config, context);
            title = result.Title;
            summary = result.Summary;
        }

        // Search-and-pick for a single file; Enter lists songs. Null when cancelled.
        static RestoreEntry PickEntry(Snapshot snapshot, bool songsOnly, OperationContext context)
        {
            while (true)
            {
                string filter = context.AskText("Search file name (Enter = " + context.Device.SongsLabel + ", q = cancel):");
                if (filter == null || filter.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                filter = filter.Trim();
                var matches = snapshot.Files.FindAll(f => (!songsOnly || context.Device.IsSong(f.Relative)) &&
                    (filter.Length == 0 ? context.Device.IsSong(f.Relative) : f.Relative.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));
                matches.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
                if (matches.Count == 0) { context.WriteLine("  No matching files."); continue; }
                var labels = new List<string>();
                foreach (RestoreEntry match in matches) labels.Add(match.Relative + " (" + FormatSize(match.Size) + ")");
                int pick = context.AskItem("Choose a file.", labels, "Search again");
                if (pick > 0) return matches[pick - 1];
            }
        }

        // Lists the song folders (folders holding a song file, e.g. a Tracker project) and lets the user pick one.
        static string PickSongFolder(Snapshot snapshot, OperationContext context)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry file in snapshot.Files)
                if (context.Device.IsSong(file.Relative)) folders.Add(Path.GetDirectoryName(file.Relative));
            var list = new List<string>(folders);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            if (list.Count == 0) { context.WriteLine("  No " + context.Device.SongsLabel + " found in this backup."); return null; }
            int pick = context.AskItem("Choose a " + Singular(context.Device.SongsLabel) + ".", list);
            return pick == 0 ? null : list[pick - 1];
        }

        // "projects" -> "project"
        static string Singular(string label)
        {
            return label.EndsWith("s") ? label.Substring(0, label.Length - 1) : label;
        }

        // Lists the bundles (folders under Bundles\ that contain files) and lets the user pick one.
        static string PickBundle(Snapshot snapshot, OperationContext context)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry file in snapshot.Files)
            {
                string[] parts = file.Relative.Split('\\');
                if (parts.Length >= 3 && parts[0].Equals(context.Device.BundlesFolder, StringComparison.OrdinalIgnoreCase))
                    names.Add(context.Device.BundlesFolder + "\\" + parts[1]);
            }
            var list = new List<string>(names);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            if (list.Count == 0) { context.WriteLine("  No bundles found. Create a bundle on the " + context.Device.Name + " before backing up."); return null; }
            int pick = context.AskItem("Choose a bundle.", list);
            return pick == 0 ? null : list[pick - 1];
        }
    }

}
