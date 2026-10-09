// Settings from M8Backup.ini next to the exe.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class M8Backup
{
    class Config
    {
        public string BackupFolder;
        public string Mode = "ask";     // ask, full or incremental
        public bool Verify = true;
        public int KeepBackups = 0;     // 0 = keep all
        public int MaxNameLength = 64;  // 0 = don't check
        public string Eject = "no";     // no, ask or yes
        public int KeepOldVersions = 0; // 0 = keep all
    }

    // Each setting with the explanation written into M8Backup.ini when it's missing from the file.
    static readonly string[][] SettingDocs =
    {
        new[] { "BackupFolder",
            "; BackupFolder: where backups go. Each backup is saved to\r\n" +
            ";   <BackupFolder>\\M8 Backups\\<date>\r\n" +
            "; Leave it empty to use your Music folder. Environment variables work,\r\n" +
            "; e.g. BackupFolder=%USERPROFILE%\\Documents or BackupFolder=D:\\Backups\r\n" +
            "BackupFolder=\r\n" },
        new[] { "Mode",
            "\r\n" +
            "; Mode: what to do when there is already a backup.\r\n" +
            ";   ask         = show a menu: full backup, incremental backup or restore\r\n" +
            ";   full        = always make a new full backup\r\n" +
            ";   incremental = always copy new and changed files into the last backup\r\n" +
            "; To restore when Mode isn't ask, run: M8Backup.exe --restore\r\n" +
            "Mode=ask\r\n" },
        new[] { "Verify",
            "\r\n" +
            "; Verify: yes = read every copied file back and compare its SHA-256 checksum\r\n" +
            "; with the original, to catch bad copies (slower). no = skip this.\r\n" +
            "Verify=yes\r\n" },
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

    // Reads M8Backup.ini next to the exe. Settings missing from the file (or the whole file) are
    // added with their defaults and explanations, so the file always shows every option.
    static Config LoadConfig()
    {
        var config = new Config { BackupFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) };
        string configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "M8Backup.ini");
        try
        {
            bool exists = File.Exists(configFile);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exists)
            {
                foreach (string line in File.ReadAllLines(configFile))
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

            var missing = new StringBuilder(exists ? "" : "; M8 Backup settings\r\n;\r\n");
            foreach (string[] setting in SettingDocs)
                if (!found.Contains(setting[0])) missing.Append(setting[1]);
            if (missing.Length > 0) File.AppendAllText(configFile, missing.ToString());
        }
        catch (Exception ex)
        {
            WriteColored("  Could not read " + configFile + ": " + ex.Message, ConsoleColor.Yellow);
        }
        return config;
    }

    static void ApplySetting(Config config, string key, string value)
    {
        string lower = value.ToLowerInvariant();
        int number;
        switch (key.ToLowerInvariant())
        {
            case "backupfolder":
                if (value.Length > 0) config.BackupFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
                return;
            case "mode":
                if (lower == "ask" || lower == "full" || lower == "incremental") config.Mode = lower;
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
                if (lower == "no" || lower == "ask" || lower == "yes") config.Eject = lower;
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
        WriteColored("  M8Backup.ini: " + key + "=" + value + " isn't valid (expected " + expected + "), using the default.",
                     ConsoleColor.Yellow);
    }
}
