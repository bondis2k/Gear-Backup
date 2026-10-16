// Full and incremental backups, the backup folders, and scanning the M8.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using static ConsoleUi;
using static Backups;

static class Backups
{
    // The date/time suffix added to old versions of updated files, e.g. "Song_2026-10-01_143000" or "..._2"

    // The full result, including whether the backup was cancelled. Title is null if nothing was done.
    internal static OperationResult RunBackup(string source, string backupRoot, string previous, bool incremental, Config config)
    {
        return new BackupEngine().Run(source, backupRoot, previous, incremental, config, OperationContext.Current);
    }

    // Returns the exit code; title/summary are for the final notification (null if nothing was done).
    internal static int Backup(string source, string backupRoot, string previous, bool incremental, Config config,
                      out string title, out string summary)
    {
        OperationResult result = RunBackup(source, backupRoot, previous, incremental, config);
        title = result.Title;
        summary = result.Summary;
        return result.ExitCode;
    }
}
