using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class M8Backup
{
    // Owns the worker lifetime and operation orchestration; the form owns display.
    sealed class GuiOperationRunner
    {
        readonly MainForm form;
        CancellationTokenSource cancellation;
        string detectedSource, detectedPnp;
        public GuiOperationRunner(MainForm owner) { form = owner; }
        public bool IsBusy { get { return cancellation != null; } }
        public bool CancellationRequested { get { return cancellation != null && cancellation.IsCancellationRequested; } }
        public void Cancel() { if (cancellation != null) cancellation.Cancel(); }

        void RunWorker(Func<OperationContext, OperationResult> action, bool previewOnly)
        {
            cancellation = new CancellationTokenSource();
            var context = new OperationContext(new GuiInteraction(form), cancellation.Token) { PreviewOnly = previewOnly };
            form.PrepareOperation();
            Task.Factory.StartNew(delegate {
                OperationResult result;
                try { using (context.Activate()) result = action(context); }
                catch (OperationCanceledException) { result = new OperationResult { Cancelled = true, Summary = "Operation cancelled." }; }
                catch (Exception ex) { result = new OperationResult { ExitCode = 1, Title = "Could not complete operation", Summary = ex.Message }; }
                form.AppendActivity(Environment.NewLine + (result.Summary ?? "No files changed.") + Environment.NewLine);
                form.BeginInvoke((Action)delegate { form.DrainDisplay(); cancellation.Dispose(); cancellation = null; form.CompleteOperation(result, previewOnly); });
            });
        }
        public void DetectSource()
        {
            RunWorker(delegate(OperationContext context) {
                string pnp;
                string found = FindM8Drive(out pnp);
                context.CheckCancellation();
                if (found == null) throw new IOException("No M8 detected. Enable USB drive mode, or browse to a card or folder.");
                form.OnUi(delegate { form.SetDetectedSource(found); detectedSource = Rooted(found); detectedPnp = pnp; return true; });
                return new OperationResult { Summary = "M8 detected: " + found };
            }, false);
        }
        public void Run(Config options, int choice, bool previewOnly)
        {
            string knownSource = detectedSource, knownPnp = detectedPnp;
            RunWorker(delegate(OperationContext context) {
                return Execute(options, choice, previewOnly, knownSource, knownPnp, context);
            }, previewOnly);
        }

        OperationResult Execute(Config options, int choice, bool previewOnly, string knownSource, string knownPnp, OperationContext context)
        {
            string histories = Path.Combine(options.BackupFolder, "M8 Backups");
            if (choice == 3)
            {
                string history = options.Profile.Length > 0 ? Path.Combine(histories, "profiles", options.Profile) : ChooseHistory(histories, null);
                if (history == null) return new OperationResult { Cancelled = true, Summary = "Check cancelled." };
                var backups = ListBackups(history);
                if (backups.Count == 0) throw new IOException("No backups found at this location.");
                backups.Reverse();
                var labels = new List<string>();
                foreach (string path in backups) labels.Add(Path.GetFileName(path) + " [" + BackupStatus(path) + "]");
                labels.Add("All backups in this history");
                int pick = form.OnUi(delegate {
                    using (var dialog = new SelectionDialog("Choose backups to check", labels))
                    {
                        if (dialog.ShowDialog(form) != DialogResult.OK) throw new OperationCanceledException();
                        return dialog.List.SelectedIndex;
                    }
                });
                var selectedBackups = pick == backups.Count ? backups : new List<string> { backups[pick] };
                using (LockHistory(history)) return new IntegrityEngine().Run(selectedBackups, context);
            }
            string selected = options.SourceFolder, pnp = null;
            if (selected.Length == 0)
            {
                selected = FindM8Drive(out pnp);
                if (selected == null) throw new IOException("Choose a source folder, or connect the M8 in USB drive mode.");
                string found = selected;
                form.OnUi(delegate { form.SetDetectedSource(found); detectedSource = Rooted(found); detectedPnp = pnp; return true; });
            }
            selected = Rooted(selected);
            if (!Directory.Exists(selected)) throw new IOException("Source folder does not exist: " + selected);
            RejectRootLinks(selected);
            if (Within(histories, selected) || Within(selected, histories)) throw new IOException("Source and backup location must not contain one another.");
            if (knownSource == selected) pnp = knownPnp;
            string root = Path.Combine(histories, "profiles", ProfileId(selected, options.Profile));
            OperationResult result;
            if (choice == 2)
            {
                root = ChooseHistory(histories, root);
                if (root == null) return new OperationResult { Cancelled = true, Summary = "Restore cancelled." };
                if (ListBackups(root).Count == 0) throw new IOException("No backups found at this location.");
                using (previewOnly ? null : LockHistory(root)) result = RunRestore(selected, ListBackups(root), options);
                result.Cancelled |= context.Cancellation.IsCancellationRequested;
            }
            else
            {
                // Read the latest state while holding the same history lock as the CLI.
                using (previewOnly ? null : LockHistory(root))
                {
                    var backups = ListBackups(root);
                    string previous = backups.Count == 0 ? null : backups[backups.Count - 1];
                    bool incremental = choice == 1 && previous != null;
                    if (choice == 1 && previous == null) OperationOutput.WriteLine("First backup for this profile: creating a full backup.");
                    if (previous != null && (BackupStatus(previous) == "incomplete" || BackupStatus(previous) == "failed"))
                    {
                        char answer = AskKey("The latest backup is " + BackupStatus(previous) + ". [R]esume, [N]ew full backup, or [C]ancel?", "rnc");
                        if (answer == 'c') return new OperationResult { Cancelled = true, Summary = "Backup cancelled." };
                        incremental = answer == 'r';
                    }
                    result = new BackupEngine().Run(selected, root, previous, incremental, options, context);
                }
            }
            if (!previewOnly && !result.Cancelled && result.ExitCode == 0 && result.Title != null && pnp != null && options.Eject != "no")
            {
                // The window can stay open while devices move to another drive letter.
                // Never eject a cached device unless it still owns this source.
                try
                {
                    string currentPnp;
                    string currentSource = FindM8Drive(out currentPnp);
                    if (currentSource == null || Rooted(currentSource) != selected || currentPnp != pnp)
                    {
                        OperationOutput.WriteLine("The detected device changed. It has been left connected.");
                        return result;
                    }
                }
                catch (Exception ex) { OperationOutput.WriteLine("Could not confirm the device for ejection: " + ex.Message); return result; }
                context.CheckCancellation();
                bool eject = options.Eject == "yes" || AskInConsole("Safely eject the M8 now? (y/n)");
                if (eject) { string error = EjectDrive(pnp); OperationOutput.WriteLine(error == null ? "M8 safely ejected." : "Could not eject: " + error); }
            }
            return result;
        }
    }
}
