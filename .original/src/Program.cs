// Finds a Dirtywave M8 connected in USB disk mode and backs up its SD card
// to <BackupFolder>\M8 Backups\<date>, or incrementally into the latest existing backup,
// or restores a backup (or a single file) to the M8. Settings are in M8Backup.ini next to the exe.
// Afterwards it offers to eject the M8 via a Windows notification.
//
// Usage: M8Backup.exe [--restore | --check] [--no-pause]
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
            result = Run(restore, check);
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
            Console.ReadKey(true);
        }
        return result;
    }

    static int Run(bool restore, bool check)
    {
        Console.WriteLine();
        Console.WriteLine("  Dirtywave M8 backup");
        Console.WriteLine("  ----------------------------------------");

        Config config = LoadConfig();
        string backupRoot = Path.Combine(config.BackupFolder, "M8 Backups");
        List<string> backups = ListBackups(backupRoot);
        string previous = backups.Count > 0 ? backups[backups.Count - 1] : null;

        // Checking backups doesn't need the M8
        if (check) return Check(backups);

        string pnpId;
        string source = FindM8Drive(out pnpId);
        if (source == null)
        {
            WriteColored("  No M8 found. Connect it by USB and turn on disk mode, then try again.", ConsoleColor.Red);
            Console.WriteLine("  (To check your backups without the M8, run: M8Backup.exe --check)");
            ShowToast("No M8 found", "Connect it by USB and turn on disk mode, then try again.", false);
            return 1;
        }
        WriteColored("  M8 found on " + source, ConsoleColor.Green);

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
                    Console.WriteLine();
                    return Check(backups);
                }
                incremental = choice == '2';
                restore = choice == '3';
            }
        }
        Console.WriteLine();

        string title, summary;
        int result = restore
            ? Restore(source, backups, config, out title, out summary)
            : Backup(source, backupRoot, previous, incremental, config, out title, out summary);
        if (title != null) Finish(pnpId, title, summary, config.Eject);
        return result;
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
