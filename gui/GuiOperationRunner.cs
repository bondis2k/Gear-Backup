using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static GearBackup.PathSafety;
using static GearBackup.Backups;
using static GearBackup.RestoreFlow;
using static GearBackup.FujiSettings;
using static GearBackup.UsbDevices;
using static GearBackup.ConsoleUi;
using static GearBackup.Session;
using static GearBackup.Histories;
using static GearBackup.MtpMirror;
using static GearBackup.HistoryLock;
using static GearBackup.BackupFolders;
using static GearBackup.FujiSettingsBackup;

namespace GearBackup.Gui
{
    // Shared operations offered by the Backup and Restore tabs.
    internal enum GuiOperation { FullBackup, IncrementalBackup, Restore, Check, CameraSettings, ZipArchive, RestoreZip }

    // Owns the worker lifetime and operation orchestration; the form owns display.
    internal sealed class GuiOperationRunner
    {
        readonly MainForm form;
        CancellationTokenSource cancellation;
        // The last drive found by detection, so a source typed as that same drive can still be ejected.
        SourceChoice detectedDrive;
        SourceChoice detectedSource;
        public GuiOperationRunner(MainForm owner) { form = owner; }
        public bool IsBusy { get { return cancellation != null; } }
        public bool CancellationRequested { get { return cancellation != null && cancellation.IsCancellationRequested; } }
        public void Cancel() { if (cancellation != null) cancellation.Cancel(); }

        // Runs one operation on a background thread with its own context and cancellation, then reports the
        // result back on the UI thread.
        void RunWorker(Func<OperationContext, OperationResult> action, bool previewOnly, bool migration = false, Action<OperationResult> finished = null)
        {
            cancellation = new CancellationTokenSource();
            var context = new OperationContext(new GuiInteraction(form), cancellation.Token) { PreviewOnly = previewOnly };
            form.PrepareOperation(migration);
            Task.Factory.StartNew(delegate {
                OperationResult result;
                try { using (context.Activate()) result = action(context); }
                catch (OperationCanceledException) { result = new OperationResult(OperationOutcome.Cancelled) { Summary = "Operation cancelled." }; }
                catch (Exception ex) { result = new OperationResult(OperationOutcome.Failed, 1) { Title = "Could not complete operation", Summary = ex.Message + HintAfter(ex) }; }
                form.AppendActivity(Environment.NewLine + (result.Summary ?? "No files changed.") + Environment.NewLine);
                form.BeginInvoke((Action)delegate {
                    form.DrainDisplay(); cancellation.Dispose(); cancellation = null; form.CompleteOperation(result, previewOnly);
                    if (finished != null && !form.IsDisposed && !form.Disposing) finished(result);
                });
            });
        }

        static string HintAfter(Exception error) { string hint = ErrorHints.For(error); return hint == null ? "" : " " + hint; }

        internal void RunMigration(Config options, RestoreRequest request, string target, bool linkHistory, Action<M8MigrationReport> completed)
        {
            M8MigrationReport report = null;
            var reader = M8SongReaders.Current;
            RunWorker(context => {
                try { report = M8CardMigration.Run(target, options, request, reader, linkHistory, context); }
                catch (OperationCanceledException)
                {
                    report = new M8MigrationReport { Backup = request.Backup, Point = request.Point, Target = target,
                        Operation = new OperationResult(OperationOutcome.Cancelled) { OutputFolder = target,
                            Summary = "Migration cancelled. Any completed files remain; verification or song checks may be incomplete." } };
                }
                return report.Operation;
            }, false, true, result => completed(report ?? new M8MigrationReport { Operation = result, Backup = request.Backup, Point = request.Point, Target = target }));
        }

        // Drives populate the source; MTP uses detection at backup time, never a previous drive path.
        internal void ShowDetected(SourceChoice found)
        {
            string information = DeviceInformation.Read(found);
            detectedDrive = found != null && found.Mtp == null ? found : null;
            detectedSource = found;
            form.OnUi(delegate {
                form.SetDetectedSource(found != null && found.Mtp == null ? found.Path : "");
                form.SetBackupSetSource(found);
                string connection = found == null ? null : found.CameraMode ?? (found.Mtp != null ? "MTP" : "USB drive · " + found.Path);
                form.ShowDevice(found == null ? null : found.Detected, connection, found != null && found.CameraSettingsMode);
                form.ShowDeviceInformation(information);
                if (found != null && found.CameraSettingsMode) form.SelectCameraSettings();
                return true;
            });
        }

        // Replaced by tests so detection doesn't depend on attached hardware.
        internal Func<DeviceProfile, SourceChoice> Detector = Session.DetectSource;

        // "only" looks for that device type (a gear icon was clicked); without it any supported device is found.
        public void DetectSource(DeviceProfile only = null)
        {
            RunWorker(delegate(OperationContext context) {
                SourceChoice found = Detector(only);
                context.CheckCancellation();
                ShowDetected(found);
                if (found == null) throw new IOException("No " + (only == null ? Devices.Names : only.Name) + " detected. Enable USB drive mode, or browse to a card or folder.");
                if (found.CameraSettingsMode) return new OperationResult(OperationOutcome.Success) { Summary = "X100VI detected in settings mode. Back up C1–C7 recipes and shooting settings with the selected operation." };
                if (found.Detected == Devices.X100VI) return new OperationResult(OperationOutcome.Success) { Summary = "X100VI detected in photo-transfer mode (USB card reader / MTP). Leave the source empty to back up photos and videos." };
                if (found.Mtp != null) return new OperationResult(OperationOutcome.Success) { Summary = found.Detected.Name + " detected over MTP. Leave the source empty to back it up." };
                return new OperationResult(OperationOutcome.Success) { Summary = found.Detected.Name + " detected: " + found.Path };
            }, false);
        }

        public void Run(Config options, GuiOperation operation, bool previewOnly)
        {
            SourceChoice known = operation == GuiOperation.RestoreZip ? detectedSource : detectedDrive;
            RunWorker(delegate(OperationContext context) {
                context.OverallProgressOnly = operation == GuiOperation.FullBackup || operation == GuiOperation.ZipArchive;
                return Execute(options, operation, known, context);
            }, previewOnly);
        }

        OperationResult Execute(Config options, GuiOperation operation, SourceChoice known, OperationContext context)
        {
            if (operation == GuiOperation.RestoreZip) return RestoreArchive(options, known, context);
            if (operation == GuiOperation.CameraSettings) return BackupCameraSettings(options, context);
            if (operation == GuiOperation.Check) return CheckBackups(options, context);
            if (operation == GuiOperation.Restore && options.RestoreSelection != null) return RestoreSelection(options, known, context);
            SourceChoice chosen = ChooseSource(options, known);
            PreparedSource prepared = PrepareSource(options, chosen, operation != GuiOperation.ZipArchive, context);
            if (operation == GuiOperation.ZipArchive) return SaveArchive(prepared, options, context);
            OperationResult result = RunPrepared(prepared, operation == GuiOperation.Restore, operation == GuiOperation.IncrementalBackup, context);
            EjectIfWanted(prepared, result, context);
            return result;
        }

        OperationResult RestoreArchive(Config options, SourceChoice known, OperationContext context)
        {
            if (known == null || known.Detected == null) throw new IOException("Detect the destination device before restoring a ZIP archive.");
            string target = options.SourceFolder;
            if (string.IsNullOrEmpty(target))
            {
                if (known.Mtp != null) throw new IOException("Choose a PC restore folder for this MTP device; transfer files back manually.");
                target = known.Path;
            }
            context.Device = known.Detected;
            Config effective = ProfileSettings.ResolveSourceSettings(options, target, context.Device);
            var restoredZip = ZipRestore.Run(options.RestoreArchive, known, target, effective,
                delegate { form.ValidateArchiveRestoreDevice(known); }, context: context);
            if (known.Mtp == null && Rooted(known.Path) == Rooted(target))
                EjectIfWanted(new PreparedSource { Source = known, Path = target, Device = known.Detected, Config = effective }, restoredZip, context);
            return restoredZip;
        }

        OperationResult BackupCameraSettings(Config options, OperationContext context)
        {
            OperationResult settingsResult = RunFujiSettingsBackup(options, context);
            form.OnUi(delegate { form.ShowDevice(Devices.X100VI, FujiModeName(true), true); return true; });
            return settingsResult;
        }

        OperationResult CheckBackups(Config options, OperationContext context)
        {
            // The same history choice and check as the CLI's; the question becomes a list dialog.
            string history = HistoryToCheck(options, context);
            if (history == null) return new OperationResult(OperationOutcome.Cancelled) { Summary = "Check cancelled." };
            var backups = ListBackups(history);
            if (backups.Count == 0) throw new IOException("No backups found at this location.");
            using (LockHistory(history)) return IntegrityEngine.RunCheck(backups, context);
        }

        OperationResult RestoreSelection(Config options, SourceChoice known, OperationContext context)
        {
            string target = options.SourceFolder;
            if (target.Length == 0 && known != null && known.Detected != null && known.Detected.Id == options.RestoreSelection.DeviceId) target = known.Path;
            if (string.IsNullOrEmpty(target)) throw new IOException("Choose a restore destination folder or a connected USB drive.");
            if (!Directory.Exists(target)) throw new IOException("Restore destination does not exist: " + target);
            OperationResult restored = RestoreCatalog.Run(target, options, options.RestoreSelection, context);
            if (known != null && Rooted(known.Path) == Rooted(target))
            {
                DeviceProfile device = Devices.Find(options.RestoreSelection.DeviceId);
                Config effective = ProfileSettings.ResolveProfileSettings(options, device.Id, System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(options.RestoreSelection.Backup)));
                EjectIfWanted(new PreparedSource { Source = known, Path = target, Device = device, Config = effective }, restored, context);
            }
            return restored;
        }

        OperationResult SaveArchive(PreparedSource prepared, Config options, OperationContext context)
        {
            string suggested = ArchiveBackup.FileName(options.FullBackupName, prepared.Device, DateTime.Now);
            string destination = form.OnUi(delegate { return form.ChooseArchiveDestination(suggested); });
            if (string.IsNullOrEmpty(destination)) return new OperationResult(OperationOutcome.Cancelled) { Summary = "ZIP archive cancelled." };
            OperationResult archived = RunArchive(prepared, destination, context);
            EjectIfWanted(prepared, archived, context);
            return archived;
        }

        SourceChoice ChooseSource(Config options, SourceChoice known)
        {
            SourceChoice chosen;
            if (options.SourceFolder.Length == 0)
            {
                chosen = Session.DetectSource();
                ShowDetected(chosen);
                if (chosen == null) throw new IOException("Choose a source folder, or connect the " + Devices.Names + ".");
            }
            // A folder chosen by hand leaves the header as it is; the detected drive typed in again keeps its
            // device and can still be ejected.
            else if (known != null && Rooted(options.SourceFolder) == Rooted(known.Path)) chosen = known;
            else chosen = new SourceChoice { Path = options.SourceFolder };

            return chosen;
        }

        // Ejects a detected drive after a successful run when the Eject preference asks for it, and only if
        // it's still the same device.
        static void EjectIfWanted(PreparedSource prepared, OperationResult result, OperationContext context)
        {
            string pnp = prepared.Source.PnpId;
            if (context.PreviewOnly || !result.Succeeded || pnp == null ||
                prepared.Config.Eject == EjectMode.No) return;
            string changed = ConfirmSameDevice(prepared.Path, pnp);
            if (changed != null) { context.WriteLine(changed); return; }
            context.CheckCancellation();
            bool eject = prepared.Config.Eject == EjectMode.Yes || context.AskYesNo("Safely eject the " + prepared.Device.Name + " now?");
            if (eject) { string error = EjectDrive(pnp); context.WriteLine(error == null ? prepared.Device.Name + " safely ejected." : "Could not eject: " + error); }
        }
    }

}
