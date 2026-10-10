// Workflow steps shared by the CLI (Program.cs) and the GUI (GuiOperationRunner.cs), so both
// front ends pick histories, lock them, resume and eject by the same rules.
using System;
using System.Collections.Generic;
using System.IO;

partial class GearBackup
{
    static string HistoriesRoot(Config config)
    {
        return AppIdentity.ExistingFolder(config.BackupFolder, AppIdentity.BackupsFolderName, AppIdentity.Legacy.BackupsFolderName);
    }

    // Checks the source against the backup location and returns this source's history folder.
    static string HistoryFor(Config config, string source)
    {
        string histories = HistoriesRoot(config);
        if (Within(histories, source) || Within(source, histories))
            throw new IOException("The backup folder and source must not contain one another.");
        return ProfileFolder(Path.Combine(histories, "profiles"), source, config.Profile);
    }

    // The history to check: the named profile, or one the user picks. Null when cancelled.
    static string HistoryToCheck(Config config)
    {
        string histories = HistoriesRoot(config);
        if (config.Profile.Length == 0) return ChooseHistory(histories, null);
        var found = new List<string>();
        foreach (string location in ConfiguredBackupLocations(config))
        {
            string path = Path.Combine(HistoriesRoot(new Config { BackupFolder = location }), "profiles", config.Profile);
            if (Directory.Exists(path) && !found.Exists(p => Rooted(p) == Rooted(path))) { RejectRootLinks(path); found.Add(path); }
        }
        if (found.Count == 1) return found[0];
        if (found.Count > 1) { int pick = AskItem("Choose the location for this profile.", found); return pick == 0 ? null : found[pick - 1]; }
        return Path.Combine(histories, "profiles", config.Profile);
    }

    // Locks a history against a second writer; a preview changes nothing, so it takes no lock.
    static IDisposable LockUnlessPreview(string history)
    {
        return PreviewOnly ? null : LockHistory(history);
    }

    static string LatestBackup(string history)
    {
        List<string> backups = ListBackups(history);
        return backups.Count == 0 ? null : backups[backups.Count - 1];
    }

    // When the latest backup didn't finish, asks whether to resume it. Returns the incremental choice
    // to use (unchanged if the latest backup is fine), or null when the user cancels.
    static bool? ConfirmResume(string previous, bool incremental, Config config)
    {
        if (previous == null) return incremental;
        string status = BackupStatus(previous);
        if (!Status.Unfinished(status)) return incremental;
        OperationOutput.WriteLine("  Latest backup is " + status + ". Resume uses " +
            (config.Thorough ? "SHA-256 content comparison." : "size and modified time (Thorough=no)."));
        char answer = Ask("Resume the unfinished backup?", new Option('r', "Resume into it"),
            new Option('n', "New full backup"), new Option('c', "Cancel"));
        if (answer == 'c') return null;
        return answer == 'r';
    }

    // Runs a backup while holding the history lock. The latest backup is read under the lock, so a
    // backup finished by another window in the meantime is seen. `incremental` is the user's choice;
    // it's ignored when there's no backup yet.
    static OperationResult BackupWithLock(string source, string history, bool incremental, Config config)
    {
        using (LockUnlessPreview(history))
        {
            string previous = LatestBackup(history);
            if (previous == null) OperationOutput.WriteLine("  First backup for this profile: creating a full backup.");
            bool? choice = ConfirmResume(previous, incremental && previous != null, config);
            if (choice == null) return new OperationResult { Cancelled = true, Summary = "Backup cancelled." };
            return RunBackup(source, history, previous, choice.Value, config);
        }
    }

    // The M8 can be unplugged or get another drive letter while a window stays open. Only eject a
    // device that still owns this source; returns null when it does, otherwise the reason.
    static string ConfirmSameDevice(string source, string pnpId)
    {
        try
        {
            string currentPnp;
            DeviceProfile currentDevice;
            string currentSource = FindDevice(out currentDevice, out currentPnp);
            if (currentSource == null || Rooted(currentSource) != Rooted(source) || currentPnp != pnpId)
                return "The detected device changed, so it was left connected.";
            return null;
        }
        catch (Exception ex) { return "Could not confirm the device for ejection: " + ex.Message; }
    }
}
