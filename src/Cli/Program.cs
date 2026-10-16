// Finds a supported USB-drive or MTP device and backs up its content
// to <BackupFolder>\Gear Backups\profiles\<profile>\<date>, or incrementally into the latest existing backup,
// or restores a backup to a selected card or folder. Settings are in GearBackup.ini next to the exe.
// Afterwards it offers to eject supported USB-drive devices via a Windows notification.
//
// Usage: GearBackup.exe [--restore | --check | --camera-settings | --zip-archive] [--source PATH] [--profile NAME] [--zip] [--output PATH] [--name NAME] [--preview] [--no-pause]
// Build: build.cmd
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static GearBackup.Backups;
using static GearBackup.FujiSettings;
using static GearBackup.MtpMirror;
using static GearBackup.AppSettings;
using static GearBackup.ConsoleUi;
using static GearBackup.Session;
using static GearBackup.Histories;
using static GearBackup.PlanReview;
using static GearBackup.SourceSelection;
using static GearBackup.Cli.Notifications;
using static GearBackup.FujiSettingsBackup;
using static GearBackup.BackupFolders;
using static GearBackup.HistoryLock;

namespace GearBackup.Cli
{
    // Entry point of GearBackup.exe.
    static class Program
    {
        static int Main(string[] args)
        {
            bool pause = Array.IndexOf(args, "--no-pause") < 0;
            bool restore = Array.IndexOf(args, "--restore") >= 0;
            bool check = Array.IndexOf(args, "--check") >= 0;
            int result;
            try
            {
                bool preview = Array.IndexOf(args, "--preview") >= 0;
                using (var cancellation = new CancellationTokenSource())
                {
                    var context = new OperationContext(new ConsoleInteraction(), cancellation.Token) { PreviewOnly = preview };
                    ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e)
                    {
                        e.Cancel = true;
                        cancellation.Cancel();
                    };
                    Console.CancelKeyPress += handler;
                    try
                    {
                        using (context.Activate()) result = Run(restore, check, args);
                        if (cancellation.IsCancellationRequested) pause = false;
                    }
                    finally { Console.CancelKeyPress -= handler; }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  Operation cancelled.");
                pause = false;
                result = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                // The operation context is gone by now, so write to the console directly.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  Error: " + ex.Message);
                string hint = ErrorHints.For(ex);
                if (hint != null) Console.WriteLine("  " + hint);
                Console.ResetColor();
                result = 1;
            }
            if (pause)
            {
                Console.WriteLine();
                Console.Write("Press any key to exit...");
                if (!Console.IsInputRedirected) Console.ReadKey(true);
            }
            return result;
        }

        internal static int Run(bool restore, bool check, string[] args, Config suppliedConfig = null)
        {
            OperationOutput.WriteLine();
            OperationOutput.WriteLine("  " + AppIdentity.Name);
            OperationOutput.WriteLine("  ----------------------------------------");

            Config config = suppliedConfig ?? LoadConfig();
            CommandLine line = CommandLine.Parse(args, config);
            line.Restore |= restore;
            line.Check |= check;
            line.Validate(config);
            if (line.RebuildCatalog != null) return RebuildCatalogCommand(line.RebuildCatalog);
            if (line.CameraSettings) return CameraSettingsCommand(config);
            if (line.Check) return CheckHistory(HistoryToCheck(config));

            SourceChoice chosen = SelectSource(config);
            if (chosen == null)
            {
                OperationOutput.WriteLine("  Source selection cancelled. Use --check to check backups without a source.");
                return 0;
            }
            PreparedSource prepared = PrepareSource(config, chosen, !line.Archive);
            WriteColored("  Source: " + prepared.Path + " (" + prepared.Device.Name + ")", ConsoleColor.Green);
            return line.Archive ? ArchiveCommand(prepared, line) : BackupOrRestoreCommand(prepared, line.Restore);
        }

        // --rebuild-catalog: rebuilds one backup set's SQLite catalog from its restore points.
        static int RebuildCatalogCommand(string folder)
        {
            string backup = Path.GetFullPath(folder).TrimEnd('\\');
            PathSafety.RejectRootLinks(backup);
            if (!Directory.Exists(backup)) throw new IOException("Backup folder does not exist: " + backup);
            if (PreviewOnly) { OperationOutput.WriteLine("  Would rebuild " + BackupDatabase.CatalogPath(backup) + " from existing checksum lists and manifests."); return 0; }
            using (LockHistory(Path.GetDirectoryName(backup))) BackupDatabase.Rebuild(backup);
            OperationOutput.WriteLine("  Catalog rebuilt: " + BackupDatabase.CatalogPath(backup));
            return 0;
        }

        // --camera-settings: saves the X100VI's recipes and shooting settings.
        static int CameraSettingsCommand(Config config)
        {
            OperationResult settingsResult = RunFujiSettingsBackup(config);
            if (settingsResult.Summary != null) OperationOutput.WriteLine("  " + settingsResult.Summary);
            return settingsResult.ExitCode;
        }

        // --zip-archive: a standalone ZIP of the source, ejecting the device afterwards when asked to.
        static int ArchiveCommand(PreparedSource prepared, CommandLine line)
        {
            Config config = prepared.Config;
            OperationContext.Current.OverallProgressOnly = true;
            string suggested = ArchiveBackup.FileName(line.Name, prepared.Device, DateTime.Now);
            string destination = ArchiveDestination(line.Output, suggested);
            if (destination == null) { OperationOutput.WriteLine("  ZIP archive cancelled."); return 0; }
            var archived = RunArchive(prepared, destination);
            if (archived.Summary != null) OperationOutput.WriteLine("  " + archived.Summary);
            string pnpId = prepared.Source.PnpId;
            if (!PreviewOnly && !archived.Cancelled && archived.ExitCode == 0 && pnpId != null && config.Eject != EjectMode.No)
            {
                string changed = ConfirmSameDevice(prepared.Path, pnpId);
                if (changed == null) Finish(pnpId, archived.Title, archived.Summary, config.Eject);
                else OperationOutput.WriteLine("  " + changed);
            }
            return archived.ExitCode;
        }

        // A backup, a restore or a check, as the options and the user's menu choice decide.
        static int BackupOrRestoreCommand(PreparedSource prepared, bool restore)
        {
            string backupRoot = prepared.History;
            OperationOutput.WriteLine("  Backup history: " + HistoryNames.DisplayName(backupRoot) + " (" + Path.GetFileName(backupRoot) + ")");
            bool incremental = false;
            int? done = ChooseOperation(prepared, ref restore, ref incremental);
            if (done != null) return done.Value;
            OperationOutput.WriteLine();
            OperationResult result = RunPrepared(prepared, restore, incremental);
            ReportResult(prepared, result);
            return result.ExitCode;
        }

        // Decides full, incremental, restore or check from the Mode setting, asking when it is "ask". A non-null
        // result means the chosen operation has already run (a check) and is the exit code.
        static int? ChooseOperation(PreparedSource prepared, ref bool restore, ref bool incremental)
        {
            Config config = prepared.Config;
            string previous = LatestBackup(prepared.History);
            bool detached = prepared.Source.Mtp != null;
            if (!restore && previous != null)
            {
                if (config.Mode == BackupMode.Incremental) incremental = true;
                else if (config.Mode == BackupMode.Ask)
                {
                    OperationOutput.WriteLine("  Last backup: " + previous);
                    OperationOutput.WriteLine();
                    var menu = new List<Option> {
                        new Option('1', "Full backup to a new folder"),
                        new Option('2', "Incremental backup into the last backup (only new and changed files)") };
                    if (!detached) menu.Add(new Option('3', "Restore from a backup to the " + prepared.Device.Name));
                    menu.Add(new Option('4', "Check backups for damaged files"));
                    char choice = Ask("What would you like to do?", menu.ToArray());
                    if (choice == '4')
                    {
                        OperationOutput.WriteLine();
                        return CheckHistory(prepared.History);
                    }
                    incremental = choice == '2';
                    restore = choice == '3';
                }
            }
            else if (!restore && previous == null && config.Mode == BackupMode.Ask && HasHistory(prepared.Global))
            {
                OperationOutput.WriteLine("  No backups yet for this profile; other histories are available.");
                var menu = new List<Option> { new Option('1', "Full backup for this profile") };
                if (!detached) menu.Add(new Option('3', "Restore from another history"));
                menu.Add(new Option('4', "Check another history"));
                char choice = Ask("What would you like to do?", menu.ToArray());
                if (choice == '4') return CheckHistory(ChooseHistory(prepared.Global, null));
                restore = choice == '3';
            }
            return null;
        }

        // Shows how an operation ended: a notification, and ejecting the device when that is wanted and still safe.
        static void ReportResult(PreparedSource prepared, OperationResult result)
        {
            Config config = prepared.Config;
            string pnpId = prepared.Source.PnpId;
            if (result.Title != null && !PreviewOnly)
            {
                if (result.Cancelled || OperationContext.Current.Cancellation.IsCancellationRequested)
                {
                    OperationOutput.WriteLine("  " + result.Summary);
                    ShowToast(result.Title, result.Summary, false);
                }
                else if (pnpId != null)
                {
                    // Same check as the GUI: never eject a device that no longer owns this source.
                    string changed = config.Eject == EjectMode.No ? null : ConfirmSameDevice(prepared.Path, pnpId);
                    if (changed == null) Finish(pnpId, result.Title, result.Summary, config.Eject);
                    else
                    {
                        OperationOutput.WriteLine("  " + changed);
                        ShowToast(result.Title, result.Summary, false);
                    }
                }
                else ShowToast(result.Title, result.Summary, false);
            }
            else if (result.Cancelled && result.Title == null && result.Summary != null)
                OperationOutput.WriteLine("  " + result.Summary);
        }

        internal static string ArchiveDestination(string output, string suggested)
        {
            if (output == null)
            {
                output = AskText("Save ZIP as (file path or existing folder; suggested name: " + suggested + "; blank cancels):");
                if (string.IsNullOrWhiteSpace(output)) return null;
            }
            output = Path.GetFullPath(output);
            if (Directory.Exists(output)) return Path.Combine(output, suggested);
            if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--output must be a .zip file path or an existing folder.");
            return output;
        }

        // Checks one history's backups (the M8 isn't involved) and shows the result as a notification.
        static int CheckHistory(string history)
        {
            if (history == null) return 0;
            if (PreviewOnly) { OperationOutput.WriteLine("  --preview applies to backups and restores; check reads files only."); return 0; }
            using (LockHistory(history)) return Check(ListBackups(history));
        }

        // Checks backups against their checksum lists; only shows a notification (the M8 isn't involved).
        static int Check(List<string> backups)
        {
            OperationResult result = IntegrityEngine.RunCheck(backups);
            if (result.Title != null) ShowToast(result.Title, result.Summary, false);
            return result.ExitCode;
        }
    }

}
