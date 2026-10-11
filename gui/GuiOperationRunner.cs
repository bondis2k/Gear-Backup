using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static PathSafety;
using static Checksums;

partial class GearBackup
{
    // The main window's operations, in the order of its Operation list.
    internal enum GuiOperation { FullBackup, IncrementalBackup, Restore, Check, CameraSettings }

    // Owns the worker lifetime and operation orchestration; the form owns display.
    sealed class GuiOperationRunner
    {
        readonly MainForm form;
        CancellationTokenSource cancellation;
        // The last drive found by detection, so a source typed as that same drive can still be ejected.
        SourceChoice detectedDrive;
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

        // Drives populate the source; MTP uses detection at backup time, never a previous drive path.
        internal void ShowDetected(SourceChoice found)
        {
            detectedDrive = found != null && found.Mtp == null ? found : null;
            form.OnUi(delegate {
                form.SetDetectedSource(found != null && found.Mtp == null ? found.Path : "");
                string connection = found == null ? null : found.CameraMode ?? (found.Mtp != null ? "MTP" : "USB drive · " + found.Path);
                form.ShowDevice(found == null ? null : found.Detected, connection);
                if (found != null && found.CameraSettingsMode) form.SelectCameraSettings();
                return true;
            });
        }

        public void DetectSource()
        {
            RunWorker(delegate(OperationContext context) {
                SourceChoice found = GearBackup.DetectSource();
                context.CheckCancellation();
                ShowDetected(found);
                if (found == null) throw new IOException("No " + Devices.Names + " detected. Enable USB drive mode, or browse to a card or folder.");
                if (found.CameraSettingsMode) return new OperationResult { Summary = "X100VI detected in settings mode. Back up C1–C7 recipes and shooting settings with the selected operation." };
                if (found.Detected == Devices.X100VI) return new OperationResult { Summary = "X100VI detected in photo-transfer mode (USB card reader / MTP). Leave the source empty to back up photos and videos." };
                if (found.Mtp != null) return new OperationResult { Summary = found.Detected.Name + " detected over MTP. Leave the source empty to back it up." };
                return new OperationResult { Summary = found.Detected.Name + " detected: " + found.Path };
            }, false);
        }

        public void Run(Config options, GuiOperation operation, bool previewOnly)
        {
            SourceChoice known = detectedDrive;
            RunWorker(delegate(OperationContext context) {
                context.OverallProgressOnly = operation == GuiOperation.FullBackup;
                return Execute(options, operation, known, context);
            }, previewOnly);
        }

        OperationResult Execute(Config options, GuiOperation operation, SourceChoice known, OperationContext context)
        {
            if (operation == GuiOperation.CameraSettings)
            {
                OperationResult settingsResult = RunFujiSettingsBackup(options);
                form.OnUi(delegate { form.ShowDevice(Devices.X100VI, FujiModeName(true)); return true; });
                return settingsResult;
            }
            if (operation == GuiOperation.Check)
            {
                // The same history choice and check as the CLI's; the question becomes a list dialog.
                string history = HistoryToCheck(options);
                if (history == null) return new OperationResult { Cancelled = true, Summary = "Check cancelled." };
                var backups = ListBackups(history);
                if (backups.Count == 0) throw new IOException("No backups found at this location.");
                using (LockHistory(history)) return RunCheck(backups);
            }

            SourceChoice chosen;
            if (options.SourceFolder.Length == 0)
            {
                chosen = GearBackup.DetectSource();
                ShowDetected(chosen);
                if (chosen == null) throw new IOException("Choose a source folder, or connect the " + Devices.Names + ".");
            }
            // A folder chosen by hand leaves the header as it is; the detected drive typed in again keeps its
            // device and can still be ejected.
            else if (known != null && Rooted(options.SourceFolder) == Rooted(known.Path)) chosen = known;
            else chosen = new SourceChoice { Path = options.SourceFolder };

            PreparedSource prepared = PrepareSource(options, chosen);
            OperationResult result = RunPrepared(prepared, operation == GuiOperation.Restore, operation == GuiOperation.IncrementalBackup);
            EjectIfWanted(prepared, result, context);
            return result;
        }

        // Ejects a detected drive after a successful run when the Eject preference asks for it, and only if
        // it's still the same device.
        static void EjectIfWanted(PreparedSource prepared, OperationResult result, OperationContext context)
        {
            string pnp = prepared.Source.PnpId;
            if (context.PreviewOnly || result.Cancelled || result.ExitCode != 0 || result.Title == null || pnp == null ||
                prepared.Config.Eject == EjectMode.No) return;
            string changed = ConfirmSameDevice(prepared.Path, pnp);
            if (changed != null) { OperationOutput.WriteLine(changed); return; }
            context.CheckCancellation();
            bool eject = prepared.Config.Eject == EjectMode.Yes || AskYesNo("Safely eject the " + prepared.Device.Name + " now?");
            if (eject) { string error = EjectDrive(pnp); OperationOutput.WriteLine(error == null ? prepared.Device.Name + " safely ejected." : "Could not eject: " + error); }
        }
    }
}
