using System;
using System.Collections.Generic;
using System.IO;
using static GearBackup.PathSafety;
using static GearBackup.FileCopy;
using static GearBackup.Snapshots;
using static GearBackup.RestoreFlow;
using static GearBackup.Formatting;
using static GearBackup.SourceScan;

namespace GearBackup
{
    // One planner per request; target changes begin only after comparison, decisions and checks.
    internal sealed class RestorePlanner
    {
        readonly OperationContext context;
        internal RestorePlanner(OperationContext context) { this.context = context; }

        sealed class Plan
        {
            internal string Target;
            internal Config Config;
            internal bool Exact;
            internal int Identical, Differing, Newer;
            internal long Bytes, MissingBytes;
            internal readonly List<RestoreEntry> Copies = new List<RestoreEntry>();
            internal readonly List<string> Removals = new List<string>(), ExtraFolders = new List<string>(), Actions = new List<string>();
            internal List<string> Folders;
            // Fingerprints bind execution to the target state shown in the preview.
            internal readonly Dictionary<string, string> TargetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, long> Sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        internal OperationResult Run(string target, Snapshot snapshot, List<RestoreEntry> items, RestoreMode mode,
            string bundle, bool exact, Config config, Action beforeRestore, string label)
        {
            var plan = new Plan { Target = target, Config = config, Exact = exact,
                Folders = snapshot.Folders.FindAll(f => mode == RestoreMode.All || (bundle != null &&
                    (f.Equals(bundle, StringComparison.OrdinalIgnoreCase) || f.StartsWith(bundle + "\\", StringComparison.OrdinalIgnoreCase)))) };
            FindExtras(plan, snapshot, items);
            string problem = CheckPaths(plan, items);
            if (problem != null)
            {
                context.WriteColored("  " + problem, ConsoleColor.Red);
                return new OperationResult(OperationOutcome.Failed, 1) { Summary = problem };
            }
            CompareFiles(plan, items);
            if (!ConfirmConflicts(plan)) return new OperationResult(OperationOutcome.Cancelled);
            AddTargetActions(plan);
            CheckSpace(plan);
            if (!context.ShowPlan((label ?? "Restore preview") + " (" + plan.Identical + " unchanged)", plan.Actions,
                plan.Bytes, config.Preview, plan.Sizes)) return OperationResult.PlanStopped(context);
            if (plan.Exact && !context.AskYesNo("Remove " + plan.Removals.Count + " extra file(s) from the selected target?"))
                return new OperationResult(OperationOutcome.Cancelled);
            if (beforeRestore != null) beforeRestore();
            var result = new RestoreEngine().Run(target, plan.Copies, plan.Removals, plan.Folders, plan.ExtraFolders, plan.TargetHashes, config, context);
            if (result.Counts != null) result.Counts.Unchanged = plan.Identical;
            return result;
        }

        void FindExtras(Plan plan, Snapshot snapshot, List<RestoreEntry> items)
        {
            if (!plan.Exact) return;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items) wanted.Add(item.Relative);
            var scan = new ScanResult();
            CollectFiles(plan.Target, scan, context);
            foreach (var file in scan.Files)
                if (!wanted.Contains(file.FullName.Substring(plan.Target.Length))) plan.Removals.Add(file.FullName.Substring(plan.Target.Length));
            var wantedFolders = new HashSet<string>(snapshot.Folders, StringComparer.OrdinalIgnoreCase);
            foreach (string folder in scan.Folders)
                if (!wantedFolders.Contains(folder.Substring(plan.Target.Length))) plan.ExtraFolders.Add(folder.Substring(plan.Target.Length));
        }

        static string CheckPaths(Plan plan, List<RestoreEntry> items)
        {
            var lengths = new PathLengthCheck();
            string root = plan.Target.TrimEnd('\\') + "\\";
            foreach (var item in items)
            {
                string target = root + item.Relative;
                lengths.File(target);
                lengths.File(target.Substring(0, target.LastIndexOf('\\')) + "\\.gearrollback-" + new string('0', 32) + ".tmp");
            }
            foreach (string folder in plan.Folders) lengths.Folder(root + folder);
            return lengths.Problem("Restore to a folder with a shorter path, for example D:\\Restore.");
        }

        void CompareFiles(Plan plan, List<RestoreEntry> items)
        {
            foreach (var item in items)
            {
                plan.Sizes[item.Relative] = item.Size;
                context.Progress(Stage.PlanningRestore, item.Relative, 0, 0);
                string target = SafePath(plan.Target, item.Relative);
                CheckParents(plan.Target, target);
                if (Directory.Exists(target)) throw new IOException("A folder blocks the file: " + item.Relative);
                // Verify every selected backup file, even if the target appears unchanged.
                ValidateEntry(item, plan.Config.Verify, context);
                string actual = Fingerprint(target, plan.Config.Thorough, context);
                plan.TargetHashes[item.Relative] = actual;
                if (EntryMatchesTarget(item, target, plan.Config.Thorough, actual, context))
                { plan.Identical++; plan.Actions.Add("Skip unchanged: " + item.Relative); continue; }
                if (actual != null)
                {
                    plan.Differing++;
                    if (File.GetLastWriteTimeUtc(target).Ticks > item.ModifiedTicks + TimeSpan.FromSeconds(2).Ticks) plan.Newer++;
                }
                else plan.MissingBytes += item.Size;
                plan.Copies.Add(item); plan.Bytes += item.Size;
                plan.Actions.Add((actual == null ? "Add: " : "Replace: ") + item.Relative);
            }
        }

        bool ConfirmConflicts(Plan plan)
        {
            if (plan.Newer > 0) context.WriteColored("  " + plan.Newer + " target file(s) are newer than this backup.", ConsoleColor.Yellow);
            if (plan.Differing == 0 || context.PreviewOnly) return true;
            char answer = context.Ask("Some target files differ from the backup.", new Option('r', "Replace them"),
                new Option('k', "Keep them (copy only missing files)"), new Option('c', "Cancel"));
            if (answer == 'c') return false;
            if (answer == 'k')
            {
                if (plan.Exact)
                {
                    context.WriteLine("  Keeping differing files also disables exact removal.");
                    plan.Exact = false; plan.Removals.Clear(); plan.ExtraFolders.Clear();
                }
                plan.Copies.RemoveAll(f => plan.TargetHashes[f.Relative] != null);
                plan.Actions.RemoveAll(line => line.StartsWith("Replace: "));
                plan.Bytes = plan.MissingBytes;
            }
            return true;
        }

        void AddTargetActions(Plan plan)
        {
            foreach (string relative in plan.Removals)
            {
                string target = SafePath(plan.Target, relative);
                plan.Sizes[relative] = new FileInfo(target).Length;
                plan.TargetHashes[relative] = Fingerprint(target, plan.Config.Thorough, context);
                plan.Actions.Add("REMOVE extra file: " + relative);
            }
            foreach (string folder in plan.ExtraFolders) plan.Actions.Add("Remove folder if empty: " + folder);
            foreach (string folder in plan.Folders)
            {
                string target = SafePath(plan.Target, folder);
                CheckParents(plan.Target, target);
                if (File.Exists(target)) throw new IOException("A file blocks the folder: " + folder);
                if (!Directory.Exists(target)) plan.Actions.Add("Create folder: " + folder);
            }
        }

        void CheckSpace(Plan plan)
        {
            long reserve = RestoreSpaceNeeded(plan.Target, plan.Copies);
            string error = CheckFreeSpace(plan.Target, reserve);
            if (error != null) throw new IOException(error);
            context.WriteLine("  Target needs up to {0} working space.", FormatSize(reserve));
            if (plan.Config.Recovery) CheckRecoverySpace(plan, reserve);
            else context.WriteLine("  Recovery copies: disabled (Recovery=no).");
            context.WriteLine("  SHA-256 integrity comparison: " + (plan.Config.Verify ? "enabled" : "disabled (Verify=no)"));
        }

        void CheckRecoverySpace(Plan plan, long targetReserve)
        {
            string root = RecoveryRoot(plan.Config);
            RejectRootLinks(root);
            if (Within(root, plan.Target) || Within(plan.Target, root)) throw new IOException("Recovery folder and target must not contain one another.");
            var paths = new HashSet<string>(plan.Removals, StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.Copies) if (plan.TargetHashes[item.Relative] != null) paths.Add(item.Relative);
            long bytes = 0;
            foreach (string path in paths) bytes += new FileInfo(SafePath(plan.Target, path)).Length;
            context.WriteLine("  Recovery: {0} in {1} ({2} existing file(s)).", FormatSize(bytes), root, paths.Count);
            bool sameVolume = string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(plan.Target), StringComparison.OrdinalIgnoreCase);
            string error = CheckFreeSpace(root, bytes + (sameVolume ? targetReserve : 0));
            if (error != null) throw new IOException(error);
        }
    }
}
