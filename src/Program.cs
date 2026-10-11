// Finds a supported USB-drive or MTP device and backs up its content
// to <BackupFolder>\Gear Backups\profiles\<profile>\<date>, or incrementally into the latest existing backup,
// or restores a backup to a selected card or folder. Settings are in GearBackup.ini next to the exe.
// Afterwards it offers to eject supported USB-drive devices via a Windows notification.
//
// Usage: GearBackup.exe [--restore | --check | --camera-settings] [--source PATH] [--profile NAME] [--zip] [--preview] [--no-pause]
// Build: build.cmd
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static Checksums;

partial class GearBackup
{
    const string AppId = AppIdentity.NotificationId;

    static int Main(string[] args)
    {
        bool pause = Array.IndexOf(args, "--no-pause") < 0;
        bool restore = Array.IndexOf(args, "--restore") >= 0;
        bool check = Array.IndexOf(args, "--check") >= 0;
        int result;
        try
        {
            PreviewOnly = Array.IndexOf(args, "--preview") >= 0;
            using (var cancellation = new CancellationTokenSource())
            {
                var context = new OperationContext(new ConsoleInteraction(), cancellation.Token) { PreviewOnly = PreviewOnly };
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
            WriteColored("  Error: " + ex.Message, ConsoleColor.Red);
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

    static int Run(bool restore, bool check, string[] args)
    {
        OperationOutput.WriteLine();
        OperationOutput.WriteLine("  " + AppIdentity.Name);
        OperationOutput.WriteLine("  ----------------------------------------");

        Config config = LoadConfig();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--source" || args[i] == "--profile")
            {
                string key = args[i];
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                ApplySetting(config, key == "--source" ? "SourceFolder" : "Profile", args[i]);
            }
            else if (args[i] == "--zip") config.Zip = true;
            else if (args[i] != "--restore" && args[i] != "--check" && args[i] != "--camera-settings" && args[i] != "--preview" && args[i] != "--no-pause")
                throw new ArgumentException("Unknown argument: " + args[i]);
        }
        if (restore && check) throw new ArgumentException("Choose --restore or --check, not both.");
        if (Array.IndexOf(args, "--camera-settings") >= 0)
        {
            if (restore || check || Array.IndexOf(args, "--source") >= 0)
                throw new ArgumentException("--camera-settings cannot be combined with --restore, --check or --source.");
            OperationResult settingsResult = RunFujiSettingsBackup(config);
            if (settingsResult.Summary != null) OperationOutput.WriteLine("  " + settingsResult.Summary);
            return settingsResult.ExitCode;
        }
        if (config.Zip && (restore || check)) throw new ArgumentException("--zip applies to backups, not to --restore or --check.");
        string histories = HistoriesRoot(config);
        if (check) return CheckHistory(HistoryToCheck(config));

        SourceChoice chosen = SelectSource(config);
        if (chosen == null)
        {
            OperationOutput.WriteLine("  Source selection cancelled. Use --check to check backups without a source.");
            return 0;
        }
        PreparedSource prepared = PrepareSource(config, chosen);
        DeviceProfile device = prepared.Device;
        MtpSource mtp = chosen.Mtp;
        string source = prepared.Path, pnpId = chosen.PnpId;
        WriteColored("  Source: " + source + " (" + device.Name + ")", ConsoleColor.Green);
        config = prepared.Config;
        histories = prepared.Histories;
        string backupRoot = prepared.History;
        OperationOutput.WriteLine("  Profile: " + Path.GetFileName(backupRoot));
        string previous = LatestBackup(backupRoot);

        bool incremental = false;
        if (!restore && previous != null)
        {
            if (config.Mode == BackupMode.Incremental)
            {
                incremental = true;
            }
            else if (config.Mode == BackupMode.Ask)
            {
                OperationOutput.WriteLine("  Last backup: " + previous);
                OperationOutput.WriteLine();
                var menu = new List<Option> {
                    new Option('1', "Full backup to a new folder"),
                    new Option('2', "Incremental backup into the last backup (only new and changed files)") };
                if (mtp == null) menu.Add(new Option('3', "Restore from a backup to the " + device.Name));
                menu.Add(new Option('4', "Check backups for damaged files"));
                char choice = Ask("What would you like to do?", menu.ToArray());
                if (choice == '4')
                {
                    OperationOutput.WriteLine();
                    return CheckHistory(backupRoot);
                }
                incremental = choice == '2';
                restore = choice == '3';
            }
        }
        else if (!restore && previous == null && config.Mode == BackupMode.Ask && HasHistory(prepared.Global))
        {
            OperationOutput.WriteLine("  No backups yet for this profile; other histories are available.");
            var menu = new List<Option> { new Option('1', "Full backup for this profile") };
            if (mtp == null) menu.Add(new Option('3', "Restore from another history"));
            menu.Add(new Option('4', "Check another history"));
            char choice = Ask("What would you like to do?", menu.ToArray());
            if (choice == '4') return CheckHistory(ChooseHistory(prepared.Global, null));
            restore = choice == '3';
        }
        OperationOutput.WriteLine();

        OperationResult result = RunPrepared(prepared, restore, incremental);

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
                string changed = config.Eject == EjectMode.No ? null : ConfirmSameDevice(source, pnpId);
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
        return result.ExitCode;
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
        OperationResult result = RunCheck(backups);
        if (result.Title != null) ShowToast(result.Title, result.Summary, false);
        return result.ExitCode;
    }
}
