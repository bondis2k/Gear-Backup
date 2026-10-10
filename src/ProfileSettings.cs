using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class GearBackup
{
    // A separate INI keeps overrides out of the global settings and preserves installer upgrades.
    static readonly string[] ProfileSettingKeys = { "BackupFolder", "PointInTimeHistory", "HardLinks", "Verify", "Thorough", "Preview",
        "KeepBackups", "KeepOldVersions", "Recovery", "RecoveryFolder", "MaxNameLength", "Eject" };
    static string ProfileSettingsPath(Config config)
    {
        return Path.Combine(config.SettingsDirectory ?? AppDomain.CurrentDomain.BaseDirectory, AppIdentity.ProfileConfigFileName);
    }
    static bool IsProfileSetting(string key) { return Array.Exists(ProfileSettingKeys, k => k.Equals(key, StringComparison.OrdinalIgnoreCase)); }
    static void ValidateSettingsScope(string scope)
    {
        if (scope.StartsWith("device:", StringComparison.OrdinalIgnoreCase))
        {
            if (Devices.Find(scope.Substring(7)) == null) throw new ArgumentException("Unknown device in settings scope.");
        }
        else if (scope.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)) ValidateProfile(scope.Substring(8));
        else throw new ArgumentException("Use device:<device id> or profile:<profile name>.");
    }
    static Dictionary<string, Dictionary<string, string>> ReadProfileSettings(Config config)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string path = ProfileSettingsPath(config);
        if (!File.Exists(path)) return sections;
        RejectRootLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Profile settings file is too large.");
        Dictionary<string, string> current = null;
        foreach (string line in File.ReadAllLines(path))
        {
            string text = line.Trim();
            if (text.Length == 0 || text.StartsWith(";") || text.StartsWith("#")) continue;
            if (text.StartsWith("[") && text.EndsWith("]"))
            {
                string scope = text.Substring(1, text.Length - 2);
                ValidateSettingsScope(scope);
                if (!sections.TryGetValue(scope, out current)) sections[scope] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int equals = text.IndexOf('=');
            if (current == null || equals <= 0) throw new IOException("Invalid profile settings line: " + text);
            string key = text.Substring(0, equals).Trim();
            if (!IsProfileSetting(key)) throw new IOException("Unsupported profile preference: " + key);
            current[key] = text.Substring(equals + 1).Trim().Trim('"');
        }
        return sections;
    }
    static Config ResolveProfileSettings(Config global, string device, string profileName)
    {
        var settings = ReadProfileSettings(global);
        Config result = global.Clone();
        foreach (string scope in new[] { "device:" + device, "profile:" + profileName })
        {
            Dictionary<string, string> values;
            if (settings.TryGetValue(scope, out values)) foreach (var entry in values) ApplySetting(result, entry.Key, entry.Value);
        }
        return result;
    }
    static Config ResolveSourceSettings(Config global, string source)
    {
        return ResolveProfileSettings(global, CurrentDevice.Id, ProfileId(source, global.Profile));
    }
    static void SaveProfileSettings(Config global, string scope, Dictionary<string, string> values)
    {
        ValidateSettingsScope(scope);
        foreach (var entry in values)
        {
            if (!IsProfileSetting(entry.Key) || entry.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("Invalid profile preference.");
        }
        string path = ProfileSettingsPath(global);
        RejectRootLinks(path);
        using (LockHistory(Path.GetDirectoryName(path)))
        {
            var sections = ReadProfileSettings(global);
            if (values.Count == 0) sections.Remove(scope); else sections[scope] = values;
            var text = new StringBuilder("; Gear Backup preferences: global -> device -> profile.\r\n; Omitted keys inherit their defaults.\r\n");
            var scopes = new List<string>(sections.Keys); scopes.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string name in scopes)
            {
                text.AppendLine().Append('[').Append(name).AppendLine("]");
                foreach (string key in ProfileSettingKeys)
                {
                    string value;
                    if (sections[name].TryGetValue(key, out value)) text.Append(key).Append('=').AppendLine(value);
                }
            }
            AtomicText(path, text.ToString());
        }
    }
    static List<string> ConfiguredBackupLocations(Config config)
    {
        var locations = new List<string>();
        locations.Add(config.BackupFolder);
        foreach (var section in ReadProfileSettings(config).Values)
        {
            string value;
            if (section.TryGetValue("BackupFolder", out value) && !string.IsNullOrWhiteSpace(value))
            {
                string path = ExpandPath(value);
                if (!locations.Exists(p => Rooted(p) == Rooted(path))) locations.Add(path);
            }
        }
        return locations;
    }
}
