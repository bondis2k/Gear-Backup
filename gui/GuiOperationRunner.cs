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
        DeviceProfile detectedDevice;
        public GuiOperationRunner(MainForm owner) { form = owner; }
        public bool IsBusy { get { return cancellation != null; } }
        public bool CancellationRequested { get { return cancellation != null && cancellation.IsCancellationRequested; } }
        public void Cancel() { if (cancellation != null) cancellation.Cancel(); }

        // Runs one operation on a background thread with its own context and cancellation, then reports the
        // result back on the UI thread.
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
                DeviceProfile device;
                string found = FindDevice(out device, out pnp);
                context.CheckCancellation();
                if (found == null) throw new IOException("No " + Devices.Names + " detected. Enable USB drive mode, or browse to a card or folder.");
                form.OnUi(delegate { form.SetDetectedSource(found); detectedSource = Rooted(found); detectedPnp = pnp; detectedDevice = device; return true; });
                return new OperationResult { Summary = device.Name + " detected: " + found };
            }, false);
        }
        public void Run(Config options, int choice, bool previewOnly)
        {
            string knownSource = detectedSource, knownPnp = detectedPnp;
            DeviceProfile knownDevice = detectedDevice;
            RunWorker(delegate(OperationContext context) {
                return Execute(options, choice, previewOnly, knownSource, knownPnp, knownDevice, context);
            }, previewOnly);
        }

        // choice is the operation list's index: 0 full backup, 1 incremental, 2 restore, 3 check.
        OperationResult Execute(Config options, int choice, bool previewOnly, string knownSource, string knownPnp,
            DeviceProfile knownDevice, OperationContext context)
        {
            string histories = HistoriesRoot(options);
            if (choice == 3)
            {
                string history = HistoryToCheck(options);
                if (history == null) return new OperationResult { Cancelled = true, Summary = "Check cancelled." };
                var backups = ListBackups(history);
                if (backups.Count == 0) throw new IOException("No backups found at this location.");
                // The same backup choice and check as the CLI's; the question becomes a list dialog.
                using (LockHistory(history)) return RunCheck(backups);
            }
            string selected = options.SourceFolder, pnp = null;
            DeviceProfile detected = null;
            if (selected.Length == 0)
            {
                selected = FindDevice(out detected, out pnp);
                if (selected == null) throw new IOException("Choose a source folder, or connect the " + Devices.Names + " in USB drive mode.");
                string found = selected;
                DeviceProfile foundDevice = detected;
                form.OnUi(delegate { form.SetDetectedSource(found); detectedSource = Rooted(found); detectedPnp = pnp; detectedDevice = foundDevice; return true; });
            }
            selected = Rooted(selected);
            if (!Directory.Exists(selected)) throw new IOException("Source folder does not exist: " + selected);
            RejectRootLinks(selected);
            if (knownSource == selected) { pnp = knownPnp; if (detected == null) detected = knownDevice; }
            context.Device = DeviceFor(options, detected);
            string root = HistoryFor(options, selected);
            OperationResult result;
            if (choice == 2)
            {
                root = ChooseHistory(histories, root);
                if (root == null) return new OperationResult { Cancelled = true, Summary = "Restore cancelled." };
                if (ListBackups(root).Count == 0) throw new IOException("No backups found at this location.");
                using (LockUnlessPreview(root)) result = RunRestore(selected, ListBackups(root), options);
                result.Cancelled |= context.Cancellation.IsCancellationRequested;
            }
            else result = BackupWithLock(selected, root, choice == 1, options);
            if (!previewOnly && !result.Cancelled && result.ExitCode == 0 && result.Title != null && pnp != null && options.Eject != EjectMode.No)
            {
                string changed = ConfirmSameDevice(selected, pnp);
                if (changed != null) { OperationOutput.WriteLine(changed); return result; }
                context.CheckCancellation();
                bool eject = options.Eject == EjectMode.Yes || AskYesNo("Safely eject the " + context.Device.Name + " now?");
                if (eject) { string error = EjectDrive(pnp); OperationOutput.WriteLine(error == null ? context.Device.Name + " safely ejected." : "Could not eject: " + error); }
            }
            return result;
        }
    }
}
