// Settings from GearBackup.ini next to the exe.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class GearBackup
{
    internal class Config
    {
        public string BackupFolder;
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
        public bool PointInTimeHistory = true; // save snapshots and their content store
        public string Device = "auto";  // "auto" (detect) or a device id from Devices.cs, e.g. "m8"
        public bool Zip = false;        // --zip / GUI checkbox: also save full backups as a zip (not saved in the ini)

        public Config Clone() { return (Config)MemberwiseClone(); }
    }

    // Each setting with the explanation written into GearBackup.ini when it's missing from the file.
    static readonly string[][] SettingDocs =
    {
        new[] { "Recovery", "; Recovery: save PC recovery copies before replacing or removing files during restore.\r\n; Default no. A recovery failure stops the restore before changing the target.\r\nRecovery=no\r\n" },
        new[] { "RecoveryFolder", "\r\n; RecoveryFolder: destination for dated recovery folders. Empty = <BackupFolder>\\" + AppIdentity.RecoveryFolderName + ".\r\n; Environment variables work. Must be outside the source card/folder.\r\nRecoveryFolder=\r\n" },
        new[] { "SourceFolder", "\r\n; SourceFolder: optional SD card drive or folder, e.g. E:\\ or D:\\M8Card.\r\n; Empty = detect the M8, or offer manual selection if detection fails.\r\nSourceFolder=\r\n" },
        new[] { "Profile", "\r\n; Profile: separate history name for this card (letters, digits, spaces, - and _).\r\n; Empty = automatic volume serial / folder identity. Use a fixed name when changing readers.\r\nProfile=\r\n" },
        new[] { "Device", "\r\n; Device: what kind of device the source is. auto = detect the connected device\r\n; (an M8 or Tracker Mini in USB disk mode, or an OP-1 Field, OP-XY or X100VI over MTP),\r\n; or name it: m8, op1-field, op-xy, tracker-mini or x100vi. Matters for a card reader or folder.\r\nDevice=auto\r\n" },
        new[] { "Thorough", "\r\n; Thorough: yes = compare file contents with SHA-256 during backup/restore,\r\n; no = compare size and modified time. Applies to resume too. Default no.\r\nThorough=no\r\n" },
        new[] { "Preview", "\r\n; Preview: show the file plan and ask before backups/restores. --preview only shows the plan.\r\nPreview=yes\r\n" },
        new[] { "HardLinks", "\r\n; HardLinks: yes = on NTFS, store the point-in-time history as hard links to the backup files\r\n; instead of second copies: about half the disk space and faster backups. The backup folder and\r\n; its history then share one copy, so editing or damaging a backup file also changes its history.\r\n; no = keep separate copies (default). Other file systems always use copies.\r\nHardLinks=no\r\n" },
        new[] { "PointInTimeHistory", "\r\n; PointInTimeHistory: yes = save restorable snapshots and a history content store (default).\r\n; no = save backup files and checksums only, avoiding the second copy of their content.\r\n; Old file versions and separate full backups are still kept according to their retention settings.\r\n; Existing snapshots are not removed. HardLinks has no effect when this is no.\r\nPointInTimeHistory=yes\r\n" },
        new[] { "BackupFolder",
            "; BackupFolder: where backups go. Each backup is saved to\r\n" +
            ";   <BackupFolder>\\" + AppIdentity.BackupsFolderName + "\\profiles\\<profile>\\<date>\r\n" +
            "; Leave it empty to use your Music folder. Environment variables work,\r\n" +
            "; e.g. BackupFolder=%USERPROFILE%\\Documents or BackupFolder=D:\\Backups\r\n" +
            "BackupFolder=\r\n" },
        new[] { "Mode",
            "\r\n" +
            "; Mode: what to do when there is already a backup.\r\n" +
            ";   ask         = show a menu: full backup, incremental backup or restore\r\n" +
            ";   full        = always make a new full backup\r\n" +
            ";   incremental = always copy new and changed files into the last backup\r\n" +
            "; To restore when Mode isn't ask, run: " + AppIdentity.DirectoryName + ".exe --restore\r\n" +
            "Mode=ask\r\n" },
        new[] { "Verify",
            "\r\n" +
            "; Verify: yes = read every copied file back and compare its SHA-256 checksum\r\n" +
            "; with the original, to catch bad copies (slower). Also checks restore sources,\r\n" +
            "; history objects and recovery copies. no = skip automatic checksum checks.\r\n" +
            "; Hashes are still recorded; Check backups explicitly verifies them. Default no.\r\n" +
            "Verify=no\r\n" },
        new[] { "KeepBackups",
            "\r\n" +
            "; KeepBackups: how many backup folders to keep. After a successful backup the\r\n" +
            "; oldest extra ones are moved to the Recycle Bin (you're asked first). 0 = keep all.\r\n" +
            "KeepBackups=0\r\n" },
        new[] { "MaxNameLength",
            "\r\n" +
            "; MaxNameLength: warn about file and folder names longer than this, which may not\r\n" +
            "; show or load properly on the M8. 0 = don't check the length.\r\n" +
            "MaxNameLength=64\r\n" },
        new[] { "Eject",
            "\r\n" +
            "; Eject: what to do with the M8 when a backup or restore is finished.\r\n" +
            ";   no  = leave it connected, don't ask\r\n" +
            ";   ask = ask in a notification (or press Y/N in the window)\r\n" +
            ";   yes = always eject it, don't ask\r\n" +
            "Eject=no\r\n" },
        new[] { "KeepOldVersions",
            "\r\n" +
            "; KeepOldVersions: how many old versions of each file to keep in a backup, when\r\n" +
            "; incremental backups rename changed files (Song_2026-10-01_143000.m8s). After an\r\n" +
            "; incremental backup the oldest extra ones are moved to the Recycle Bin (you're\r\n" +
            "; asked first). 0 = keep all.\r\n" +
            "KeepOldVersions=0\r\n" },
    };

    // Reads GearBackup.ini next to the exe. Settings missing from it are appended with their default and
    // explanation, so the file always documents every option. A --preview run doesn't write the file.
    static Config LoadConfig()
    {
        return LoadConfig(AppDomain.CurrentDomain.BaseDirectory);
    }

    static Config LoadConfig(string directory)
    {
        var config = new Config { BackupFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) };
        string configFile = Path.Combine(directory, AppIdentity.ConfigFileName);
        string legacyFile = Path.Combine(directory, AppIdentity.Legacy.ConfigFileName);
        try
        {
            bool migrate = !File.Exists(configFile) && File.Exists(legacyFile);
            string readFile = migrate ? legacyFile : configFile;
            bool exists = File.Exists(readFile);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exists)
            {
                foreach (string line in File.ReadAllLines(readFile))
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
            foreach (string[] setting in SettingDocs)
                if (!found.Contains(setting[0])) missing.Append(setting[1]);
            if (!PreviewOnly)
            {
                if (migrate)
                {
                    // Preserve values and comments; only update product names in comment lines.
                    var text = new StringBuilder();
                    foreach (string line in File.ReadAllLines(readFile))
                    {
                        string trimmed = line.TrimStart();
                        text.AppendLine(trimmed.StartsWith(";") || trimmed.StartsWith("#")
                            ? line.Replace(AppIdentity.Legacy.DirectoryName, AppIdentity.DirectoryName)
                                  .Replace(AppIdentity.Legacy.Name, AppIdentity.Name)
                                  .Replace(AppIdentity.Legacy.RecoveryFolderName, AppIdentity.RecoveryFolderName) : line);
                    }
                    text.Append(missing);
                    AtomicText(configFile, text.ToString());
                }
                else if (missing.Length > 0) File.AppendAllText(configFile, missing.ToString());
            }
        }
        catch (Exception ex)
        {
            WriteColored("  Could not read " + configFile + ": " + ex.Message, ConsoleColor.Yellow);
        }
        return config;
    }

    // Preserve comments and unrelated keys when the GUI saves shared preferences.
    static void SaveConfig(Config config)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppIdentity.ConfigFileName);
        SaveConfig(config, path);
    }

    static void SaveConfig(Config config, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "BackupFolder", config.BackupFolder }, { "SourceFolder", config.SourceFolder }, { "Profile", config.Profile },
            { "Mode", config.Mode.ToString().ToLowerInvariant() }, { "Verify", config.Verify ? "yes" : "no" },
            { "Thorough", config.Thorough ? "yes" : "no" }, { "Preview", config.Preview ? "yes" : "no" },
            { "HardLinks", config.HardLinks ? "yes" : "no" }, { "Device", config.Device },
            { "PointInTimeHistory", config.PointInTimeHistory ? "yes" : "no" },
            { "Recovery", config.Recovery ? "yes" : "no" }, { "RecoveryFolder", config.RecoveryFolder },
            { "KeepBackups", config.KeepBackups.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "KeepOldVersions", config.KeepOldVersions.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "MaxNameLength", config.MaxNameLength.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "Eject", config.Eject.ToString().ToLowerInvariant() }
        };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        if (File.Exists(path)) foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            int equals = trimmed.IndexOf('=');
            string key = equals < 0 ? "" : trimmed.Substring(0, equals).Trim();
            string value;
            if (!trimmed.StartsWith(";") && !trimmed.StartsWith("#") && values.TryGetValue(key, out value))
            {
                text.Append(key).Append('=').Append(value).AppendLine();
                found.Add(key);
            }
            else text.AppendLine(line);
        }
        foreach (string[] setting in SettingDocs)
            if (!found.Contains(setting[0])) text.Append(setting[0]).Append('=').Append(values[setting[0]]).AppendLine();
        AtomicText(path, text.ToString());
    }

    // Applies one key=value. An invalid value prints a warning and keeps the default.
    static void ApplySetting(Config config, string key, string value)
    {
        string lower = value.ToLowerInvariant();
        int number;
        switch (key.ToLowerInvariant())
        {
            case "recovery":
            case "thorough":
            case "preview":
            case "hardlinks":
            case "pointintimehistory":
                bool enabled;
                if (lower == "yes" || lower == "true" || lower == "1" || lower == "on") enabled = true;
                else if (lower == "no" || lower == "false" || lower == "0" || lower == "off") enabled = false;
                else { WarnSetting(key, value, "yes or no"); return; }
                if (key.Equals("recovery", StringComparison.OrdinalIgnoreCase)) config.Recovery = enabled;
                else if (key.Equals("thorough", StringComparison.OrdinalIgnoreCase)) config.Thorough = enabled;
                else if (key.Equals("hardlinks", StringComparison.OrdinalIgnoreCase)) config.HardLinks = enabled;
                else if (key.Equals("pointintimehistory", StringComparison.OrdinalIgnoreCase)) config.PointInTimeHistory = enabled;
                else config.Preview = enabled;
                return;
            case "device":
                if (lower == "auto" || Devices.Find(lower) != null) config.Device = lower;
                else WarnSetting(key, value, "auto or " + string.Join(", ", Array.ConvertAll(Devices.All, d => d.Id)));
                return;
            case "recoveryfolder": config.RecoveryFolder = ExpandPath(value); return;
            case "sourcefolder": config.SourceFolder = ExpandPath(value); return;
            case "profile":
                if (value.Length > 0) ValidateProfile(value);
                config.Profile = value; return;
            case "backupfolder":
                if (value.Length > 0) config.BackupFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
                return;
            case "mode":
                if (lower == "ask") config.Mode = BackupMode.Ask;
                else if (lower == "full") config.Mode = BackupMode.Full;
                else if (lower == "incremental") config.Mode = BackupMode.Incremental;
                else if (value.Length > 0) WarnSetting(key, value, "ask, full or incremental");
                return;
            case "verify":
                if (lower == "yes" || lower == "true" || lower == "1" || lower == "on") config.Verify = true;
                else if (lower == "no" || lower == "false" || lower == "0" || lower == "off") config.Verify = false;
                else if (value.Length > 0) WarnSetting(key, value, "yes or no");
                return;
            case "keepbackups":
                if (int.TryParse(value, out number) && number >= 0) config.KeepBackups = number;
                else if (value.Length > 0) WarnSetting(key, value, "a number, 0 = keep all");
                return;
            case "eject":
                if (lower == "no") config.Eject = EjectMode.No;
                else if (lower == "ask") config.Eject = EjectMode.Ask;
                else if (lower == "yes") config.Eject = EjectMode.Yes;
                else if (value.Length > 0) WarnSetting(key, value, "no, ask or yes");
                return;
            case "keepoldversions":
                if (int.TryParse(value, out number) && number >= 0) config.KeepOldVersions = number;
                else if (value.Length > 0) WarnSetting(key, value, "a number, 0 = keep all");
                return;
            case "maxnamelength":
                if (int.TryParse(value, out number) && number >= 0) config.MaxNameLength = number;
                else if (value.Length > 0) WarnSetting(key, value, "a number, 0 = don't check");
                return;
        }
    }

    static void WarnSetting(string key, string value, string expected)
    {
        WriteColored("  " + AppIdentity.ConfigFileName + ": " + key + "=" + value + " isn't valid (expected " + expected + "), using the default.",
                     ConsoleColor.Yellow);
    }
}
