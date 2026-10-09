// Finds a Dirtywave M8 connected in USB disk mode and backs up its SD card
// to <BackupFolder>\M8 Backups\profiles\<profile>\<date>, or incrementally into the latest existing backup,
// or restores a backup (or a single file) to the M8. Settings are in M8Backup.ini next to the exe.
// Afterwards it offers to eject the M8 via a Windows notification.
//
// Usage: M8Backup.exe [--restore | --check] [--source PATH] [--profile NAME] [--preview] [--no-pause]
// Build: build.cmd
using System;
using System.Collections.Generic;
using System.IO;

partial class M8Backup
{
    const string AppId = "Dirtywave.M8Backup";

    static int Main(string[] args)
    {
        bool pause = Array.IndexOf(args, "--no-pause") < 0;
        bool restore = Array.IndexOf(args, "--restore") >= 0;
        bool check = Array.IndexOf(args, "--check") >= 0;
        int result;
        try
        {
            PreviewOnly = Array.IndexOf(args, "--preview") >= 0;
            result = Run(restore, check, args);
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
        Console.WriteLine();
        Console.WriteLine("  Dirtywave M8 backup");
        Console.WriteLine("  ----------------------------------------");

        Config config = LoadConfig();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--source" || args[i] == "--profile")
            {
                string key = args[i];
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                ApplySetting(config, key == "--source" ? "SourceFolder" : "Profile", args[i]);
            }
            else if (args[i] != "--restore" && args[i] != "--check" && args[i] != "--preview" && args[i] != "--no-pause")
                throw new ArgumentException("Unknown argument: " + args[i]);
        }
        if (restore && check) throw new ArgumentException("Choose --restore or --check, not both.");
        string histories = Path.Combine(config.BackupFolder, "M8 Backups");
        if (check)
        {
            string history = config.Profile.Length > 0 ? Path.Combine(histories, "profiles", config.Profile) : ChooseHistory(histories, null);
            if (history == null) return 0;
            if (PreviewOnly) { Console.WriteLine("  --preview applies to backups and restores; check reads files only."); return 0; }
            using (LockHistory(history)) return Check(ListBackups(history));
        }
        string pnpId;
        string source = SelectSource(config, out pnpId);
        if (source == null)
        {
            Console.WriteLine("  Source selection cancelled. Use --check to check backups without a source.");
            return 0;
        }
        WriteColored("  Source: " + source, ConsoleColor.Green);
        if (Within(histories, source) || Within(source, histories))
            throw new IOException("The backup folder and source must not contain one another.");
        string profile = ProfileId(source, config.Profile);
        string backupRoot = Path.Combine(histories, "profiles", profile);
        Console.WriteLine("  Profile: " + profile);
        List<string> backups = ListBackups(backupRoot);
        string previous = backups.Count > 0 ? backups[backups.Count - 1] : null;

        bool incremental = false;
        if (!restore && previous != null)
        {
            if (config.Mode == "incremental")
            {
                incremental = true;
            }
            else if (config.Mode == "ask")
            {
                Console.WriteLine("  Last backup: " + previous);
                Console.WriteLine();
                Console.WriteLine("    1) Full backup to a new folder");
                Console.WriteLine("    2) Incremental backup into the last backup (only new and changed files)");
                Console.WriteLine("    3) Restore from a backup to the M8");
                Console.WriteLine("    4) Check backups for damaged files");
                char choice = AskKey("  Choose 1, 2, 3 or 4: ", "1234");
                if (choice == '4')
                {
                    if (PreviewOnly) { Console.WriteLine("  Preview only; check not started."); return 0; }
                    Console.WriteLine();
                    using (LockHistory(backupRoot)) return Check(backups);
                }
                incremental = choice == '2';
                restore = choice == '3';
            }
        }
        else if (!restore && previous == null && config.Mode == "ask" && HasHistory(histories))
        {
            Console.WriteLine("  No backups yet for this profile; other histories are available.");
            Console.WriteLine("    1) Full backup for this profile");
            Console.WriteLine("    3) Restore from another history");
            Console.WriteLine("    4) Check another history");
            char choice = AskKey("  Choose 1, 3 or 4: ", "134");
            restore = choice == '3';
            if (choice == '4')
            {
                if (PreviewOnly) { Console.WriteLine("  Preview only; check not started."); return 0; }
                string selected = ChooseHistory(histories, null);
                if (selected == null) return 0;
                using (LockHistory(selected)) return Check(ListBackups(selected));
            }
        }
        Console.WriteLine();
        if (!restore && previous != null && (BackupStatus(previous) == "incomplete" || BackupStatus(previous) == "failed"))
        {
            Console.WriteLine("  Latest backup is " + BackupStatus(previous) + ". Resume uses " +
                (config.Thorough ? "SHA-256 content comparison." : "size and modified time (Thorough=no)."));
            char answer = AskKey("  [R]esume into it, [N]ew full backup, or [C]ancel? ", "rnc");
            if (answer == 'c') return 0;
            incremental = answer == 'r';
        }
        if (restore)
        {
            backupRoot = ChooseHistory(histories, backupRoot);
            if (backupRoot == null) return 0;
            backups = ListBackups(backupRoot);
        }
        // A preview does not create a lock file or backup directories.
        FileStream historyLock = PreviewOnly ? null : LockHistory(backupRoot);
        using (historyLock)
        {
            string title, summary;
            int result = restore
                ? Restore(source, backups, config, out title, out summary)
                : Backup(source, backupRoot, previous, incremental, config, out title, out summary);
            if (title != null && !PreviewOnly)
            {
                if (pnpId != null) Finish(pnpId, title, summary, config.Eject);
                else ShowToast(title, summary, false);
            }
            return result;
        }
    }

    // Checks backups against their checksum lists; only shows a notification (the M8 isn't involved).
    static int Check(List<string> backups)
    {
        string title, summary;
        int result = CheckBackups(backups, out title, out summary);
        if (title != null) ShowToast(title, summary, false);
        return result;
    }
}
