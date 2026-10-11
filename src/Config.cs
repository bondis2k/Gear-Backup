// Settings from GearBackup.ini in the installed or portable settings folder. Every setting is described once, in the Settings table:
// loading, saving, the explanations written into the file, device/profile overrides and the Profiles
// window all work from it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using static PathSafety;
using static ConsoleUi;
using static SettingsLocation;
using static Workflow;
using static AppSettings;

static class AppSettings
{
    static Setting YesNo(string key, string label, Func<Config, bool> get, Action<Config, bool> set, string doc)
    {
        return new Setting { Key = key, Label = label, Doc = doc, Choices = new[] { "yes", "no" },
            Get = c => get(c) ? "yes" : "no",
            Set = (c, value) => {
                string lower = value.ToLowerInvariant();
                if (lower == "yes" || lower == "true" || lower == "1" || lower == "on") set(c, true);
                else if (lower == "no" || lower == "false" || lower == "0" || lower == "off") set(c, false);
                else return "yes or no";
                return null;
            } };
    }

    static Setting Count(string key, string label, Func<Config, int> get, Action<Config, int> set, string zeroMeans, string doc)
    {
        return new Setting { Key = key, Label = label, Doc = doc,
            Get = c => get(c).ToString(CultureInfo.InvariantCulture),
            Set = (c, value) => {
                int number;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 0)
                    return "a number, 0 = " + zeroMeans;
                set(c, number);
                return null;
            } };
    }

    // A setting with a fixed list of values; `values` are what the INI holds, lower case.
    static Setting Choice(string key, string label, string[] values, Func<Config, string> get, Action<Config, string> set, string doc)
    {
        return new Setting { Key = key, Label = label, Doc = doc, Choices = values, Get = get,
            Set = (c, value) => {
                string lower = value.ToLowerInvariant();
                if (Array.IndexOf(values, lower) < 0) return string.Join(", ", values, 0, values.Length - 1) + " or " + values[values.Length - 1];
                set(c, lower);
                return null;
            } };
    }

    // In the order explanations are added to a new GearBackup.ini; preferences are listed in the same order.
    internal static readonly Setting[] Settings =
    {
        Choice("Theme", null, new[] { "original", "original-dark", "retro" }, c => c.Theme, (c, v) => c.Theme = v,
            "; Theme: GUI colour palette. original = light gray and teal (default); original-dark = charcoal and teal;\r\n" +
            "; retro = teal, navy, gray and cream. Help opens the matching offline manual.\r\n"),
        new Setting { Key = "BackupFolder", Label = "Backup location", Location = true,
            Get = c => c.BackupFolder,
            Set = (c, value) => { c.BackupFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)); return null; },
            Doc = "; BackupFolder: where backups go. Each backup is saved to\r\n" +
                  ";   <BackupFolder>\\" + AppIdentity.BackupsFolderName + "\\profiles\\<profile>\\<date>\r\n" +
                  "; Leave it empty to use %USERPROFILE%\\Backup. Environment variables work,\r\n" +
                  "; e.g. BackupFolder=%USERPROFILE%\\Documents or BackupFolder=D:\\Backups\r\n" },
        YesNo("SingleContentStore", "Single content store", c => c.SingleContentStore, (c, v) => c.SingleContentStore = v,
            "; SingleContentStore: yes = store each distinct file content once and save a restore point for every backup run.\r\n" +
            "; Restore through the app; there is no separate browsable copy. Set no for plain, browsable backup folders.\r\n" +
            "; Default yes.\r\n"),
        YesNo("PointInTimeHistory", "Point-in-time history", c => c.PointInTimeHistory, (c, v) => c.PointInTimeHistory = v,
            "; PointInTimeHistory: yes = save restorable snapshots and a history content store.\r\n" +
            "; no (default) = save backup files and checksums only, avoiding the second copy of their content.\r\n" +
            "; Old file versions and separate full backups are still kept according to their retention settings.\r\n" +
            "; Existing snapshots are not removed. HardLinks has no effect when this is no.\r\n" +
            "; Ignored with SingleContentStore=yes: that mode always saves restore points.\r\n"),
        YesNo("HardLinks", "Hard-linked history", c => c.HardLinks, (c, v) => c.HardLinks = v,
            "; HardLinks: yes = on NTFS, store the point-in-time history as hard links to the backup files\r\n" +
            "; instead of second copies: about half the disk space and faster backups. The backup folder and\r\n" +
            "; its history then share one copy, so editing or damaging a backup file also changes its history.\r\n" +
            "; no = keep separate copies (default). Other file systems always use copies.\r\n"),
        YesNo("Verify", "Verify copied files", c => c.Verify, (c, v) => c.Verify = v,
            "; Verify: yes = read every copied file back and compare its SHA-256 checksum\r\n" +
            "; with the original, to catch bad copies (slower). Also checks restore sources,\r\n" +
            "; history objects and recovery copies. no = skip automatic checksum checks.\r\n" +
            "; Hashes are still recorded; Check backups explicitly verifies them. Default no.\r\n"),
        YesNo("Thorough", "Compare file contents", c => c.Thorough, (c, v) => c.Thorough = v,
            "; Thorough: yes = compare file contents with SHA-256 during backup/restore,\r\n" +
            "; no = compare size and modified time. Applies to resume too. Default no.\r\n"),
        YesNo("Preview", "Review changes", c => c.Preview, (c, v) => c.Preview = v,
            "; Preview: show the file plan and ask before backups/restores. --preview only shows the plan.\r\n"),
        Count("KeepBackups", "Backups to keep", c => c.KeepBackups, (c, v) => c.KeepBackups = v, "keep all",
            "; KeepBackups: how many backup folders to keep. After a successful backup the\r\n" +
            "; oldest extra ones are moved to the Recycle Bin (you're asked first). 0 = keep all.\r\n"),
        Count("KeepOldVersions", "Old versions to keep", c => c.KeepOldVersions, (c, v) => c.KeepOldVersions = v, "keep all",
            "; KeepOldVersions: how many old versions of each file to keep in a backup, when\r\n" +
            "; incremental backups rename changed files (Song_2026-10-01_143000.m8s). After an\r\n" +
            "; incremental backup the oldest extra ones are moved to the Recycle Bin (you're\r\n" +
            "; asked first). 0 = keep all.\r\n"),
        YesNo("Recovery", "Recovery copies", c => c.Recovery, (c, v) => c.Recovery = v,
            "; Recovery: save PC recovery copies before replacing or removing files during restore.\r\n" +
            "; Default no. A recovery failure stops the restore before changing the target.\r\n"),
        new Setting { Key = "RecoveryFolder", Label = "Recovery location", Location = true, AppliesEmpty = true,
            Get = c => c.RecoveryFolder, Set = (c, value) => { c.RecoveryFolder = ExpandPath(value); return null; },
            Doc = "; RecoveryFolder: destination for dated recovery folders. Empty = <BackupFolder>\\" + AppIdentity.RecoveryFolderName + ".\r\n" +
                  "; Environment variables work. Must be outside the source card/folder.\r\n" },
        Count("MaxNameLength", "Name warning length", c => c.MaxNameLength, (c, v) => c.MaxNameLength = v, "don't check",
            "; MaxNameLength: warn about file and folder names longer than this, which may not\r\n" +
            "; show or load properly on the device. 0 = don't check the length.\r\n"),
        Choice("Eject", "Eject after completion", new[] { "no", "ask", "yes" }, c => c.Eject.ToString().ToLowerInvariant(),
            (c, v) => c.Eject = v == "yes" ? EjectMode.Yes : v == "ask" ? EjectMode.Ask : EjectMode.No,
            "; Eject: what to do with a USB-drive device (M8, Tracker Mini) when a backup or restore is finished.\r\n" +
            ";   no  = leave it connected, don't ask\r\n" +
            ";   ask = ask in a notification (or press Y/N in the window)\r\n" +
            ";   yes = always eject it, don't ask\r\n"),
        Choice("Mode", null, new[] { "ask", "full", "incremental" }, c => c.Mode.ToString().ToLowerInvariant(),
            (c, v) => c.Mode = v == "full" ? BackupMode.Full : v == "incremental" ? BackupMode.Incremental : BackupMode.Ask,
            "; Mode: what to do when there is already a backup.\r\n" +
            ";   ask         = show a menu: full backup, incremental backup or restore\r\n" +
            ";   full        = always make a new full backup\r\n" +
            ";   incremental = always copy new and changed files into the last backup\r\n" +
            "; To restore when Mode isn't ask, run: " + AppIdentity.DirectoryName + ".exe --restore\r\n"),
        new Setting { Key = "SourceFolder", AppliesEmpty = true,
            Get = c => c.SourceFolder, Set = (c, value) => { c.SourceFolder = ExpandPath(value); return null; },
            Doc = "; SourceFolder: optional SD card drive or folder, e.g. E:\\ or D:\\M8Card.\r\n" +
                  "; Empty = detect a connected device, or offer manual selection if detection fails.\r\n" },
        new Setting { Key = "Profile", AppliesEmpty = true,
            Get = c => c.Profile, Set = (c, value) => { if (value.Length > 0) ValidateProfile(value); c.Profile = value; return null; },
            Doc = "; Profile: separate history name for this card (letters, digits, spaces, - and _).\r\n" +
                  "; Empty = automatic volume serial / folder identity. Use a fixed name when changing readers.\r\n" },
        new Setting { Key = "Device", Get = c => c.Device,
            Set = (c, value) => {
                string lower = value.ToLowerInvariant();
                if (lower != "auto" && Devices.Find(lower) == null) return "auto or " + string.Join(", ", Array.ConvertAll(Devices.All, d => d.Id));
                c.Device = lower;
                return null;
            },
            Doc = "; Device: what kind of device the source is. auto = detect the connected device\r\n" +
                  "; (an M8 or Tracker Mini in USB disk mode, or an OP-1 Field, OP-XY or X100VI over MTP),\r\n" +
                  "; or name it: m8, op1-field, op-xy, tracker-mini or x100vi. Matters for a card reader or folder.\r\n" },
    };

    internal static Setting FindSetting(string key)
    {
        foreach (Setting setting in Settings)
            if (string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase)) return setting;
        return null;
    }

    // The explanation and default value written for a setting missing from GearBackup.ini.
    static string SettingDocumentation(Setting setting)
    {
        return setting.Doc + setting.Key + "=" + setting.Get(new Config { BackupFolder = "" }) + "\r\n";
    }

    internal static string DefaultBackupFolder()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Backup");
    }

    // Reads GearBackup.ini from the selected settings folder. Missing settings are appended with their default and
    // explanation, so the file always documents every option. A --preview run doesn't write the file.
    internal static Config LoadConfig()
    {
        return LoadApplicationConfig(AppDomain.CurrentDomain.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    internal static Config LoadConfig(string directory)
    {
        var config = new Config { SettingsDirectory = directory };
        string configFile = Path.Combine(directory, AppIdentity.ConfigFileName);
        try
        {
            string readFile = configFile;
            bool exists = File.Exists(readFile);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exists)
            {
                foreach (string line in Records.ReadLines(readFile, Records.SettingsLimit, "Settings file"))
                {
                    string trimmed = line.Trim();
                    int equals = trimmed.IndexOf('=');
                    if (trimmed.StartsWith(";") || trimmed.StartsWith("#") || equals < 0) continue;
                    string key = trimmed.Substring(0, equals).Trim();
                    string value = trimmed.Substring(equals + 1).Trim().Trim('"');
                    found.Add(key);
                    ApplySetting(config, key, value);
                }
            }

            var missing = new StringBuilder(exists ? "" : "; " + AppIdentity.Name + " settings\r\n;\r\n");
            bool first = !exists;
            foreach (Setting setting in Settings)
            {
                if (found.Contains(setting.Key)) continue;
                if (!first) missing.Append("\r\n");
                missing.Append(SettingDocumentation(setting));
                first = false;
            }
            if (!PreviewOnly)
            {
                if (missing.Length > 0) File.AppendAllText(configFile, missing.ToString());
            }
        }
        catch (Exception ex)
        {
            WriteColored("  Could not read " + configFile + ": " + ex.Message, ConsoleColor.Yellow);
        }
        return config;
    }

    // Preserve comments and unrelated keys when the GUI saves shared preferences.
    internal static void SaveConfig(Config config)
    {
        string path = Path.Combine(config.SettingsDirectory ?? DefaultSettingsDirectory(), AppIdentity.ConfigFileName);
        SaveConfig(config, path);
    }

    internal static void SaveConfig(Config config, string path)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        if (File.Exists(path)) foreach (string line in Records.ReadLines(path, Records.SettingsLimit, "Settings file"))
        {
            string trimmed = line.Trim();
            int equals = trimmed.IndexOf('=');
            Setting setting = equals < 0 || trimmed.StartsWith(";") || trimmed.StartsWith("#") ? null : FindSetting(trimmed.Substring(0, equals).Trim());
            if (setting != null)
            {
                text.Append(setting.Key).Append('=').Append(setting.Get(config)).AppendLine();
                found.Add(setting.Key);
            }
            else text.AppendLine(line);
        }
        foreach (Setting setting in Settings)
            if (!found.Contains(setting.Key)) text.Append(setting.Key).Append('=').Append(setting.Get(config)).AppendLine();
        AtomicText(path, text.ToString());
    }

    // Applies one key=value. An invalid value prints a warning and keeps the current value; an empty one
    // keeps it too, except for paths and names, where empty means "none". Unknown keys are ignored.
    internal static void ApplySetting(Config config, string key, string value)
    {
        Setting setting = FindSetting(key);
        if (setting == null || (value.Length == 0 && !setting.AppliesEmpty)) return;
        string expected = setting.Set(config, value);
        if (expected != null) WarnSetting(key, value, expected);
    }

    // Null when `value` is valid for the setting, otherwise what was expected.
    internal static string CheckSettingValue(Setting setting, string value)
    {
        if (value.Length == 0) return setting.AppliesEmpty || setting.Key == "BackupFolder" ? null : "a value";
        return setting.Set(new Config(), value);
    }

    static void WarnSetting(string key, string value, string expected)
    {
        WriteColored("  " + AppIdentity.ConfigFileName + ": " + key + "=" + value + " isn't valid (expected " + expected + "), using the default.",
                     ConsoleColor.Yellow);
    }
}

internal class Config
{
    public string BackupFolder = DefaultBackupFolder();
    public BackupMode Mode = BackupMode.Ask;
    public bool Verify = false;
    public int KeepBackups = 0;     // 0 = keep all
    public int MaxNameLength = 64;  // 0 = don't check
    public EjectMode Eject = EjectMode.No;
    public int KeepOldVersions = 0; // 0 = keep all
    public bool Recovery = false;
    public string RecoveryFolder = "";
    public string SourceFolder = "";
    public string Profile = "";
    public bool Thorough = false;
    public bool Preview = true;
    public bool HardLinks = false;  // share history content with the backup files (NTFS)
    public bool PointInTimeHistory = false; // opt in to snapshots and their content store
    public bool SingleContentStore = true; // snapshots backed by one immutable content store
    public string Device = "auto";  // "auto" (detect) or a device id from Devices.cs, e.g. "m8"
    public string Theme = "original"; // global GUI colour palette
    public bool Zip = false;        // --zip / GUI checkbox: also save full backups as a zip (not saved in the ini)

    public Config Clone() { return (Config)MemberwiseClone(); }
    internal string FullBackupName { get; set; }
    internal string IncrementalBackup { get; set; }
    internal RestoreRequest RestoreSelection { get; set; }
    internal string SettingsDirectory { get; set; }
}

// One setting: its INI key, the explanation written into GearBackup.ini when it's missing, and how its
// value is read and written. Preferences that can differ per device or profile have a Label, shown
// in the Profiles window.
internal sealed class Setting
{
    public string Key, Doc, Label;
    public string[] Choices;         // the allowed values of a yes/no or list setting; null otherwise
    public bool Location;            // a folder, which the Profiles window can browse for
    public bool AppliesEmpty;        // an empty value is applied (a path or name); otherwise it means "default"
    public Func<Config, string> Get; // the value as written in the INI
    // Applies a value; returns what was expected when the value isn't valid, otherwise null.
    public Func<Config, string, string> Set;
    public bool IsPreference { get { return Label != null; } }
}
