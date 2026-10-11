// Device and profile preferences: overrides of the global settings, kept in GearBackup.Profiles.ini
// in the shared settings folder. A separate file keeps them out of GearBackup.ini, and installers never touch either.
// Priority: global settings, then the device's preferences ([device:m8]), then the profile's
// ([profile:Studio]). Which settings can be overridden is in the Settings table (those with a Label).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static PathSafety;

partial class GearBackup
{
    static string ProfileSettingsPath(Config config)
    {
        return Path.Combine(config.SettingsDirectory ?? DefaultSettingsDirectory(), AppIdentity.ProfileConfigFileName);
    }

    // The preference with this key, or null when it isn't one that can be overridden.
    static Setting FindPreference(string key)
    {
        Setting setting = FindSetting(key);
        return setting != null && setting.IsPreference ? setting : null;
    }

    // A section name: "device:<device id>" or "profile:<profile name>".
    static void ValidateSettingsScope(string scope)
    {
        if (scope.StartsWith("device:", StringComparison.OrdinalIgnoreCase))
        {
            if (Devices.Find(scope.Substring(7)) == null) throw new ArgumentException("Unknown device in settings scope.");
        }
        else if (scope.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)) ValidateProfile(scope.Substring(8));
        else throw new ArgumentException("Use device:<device id> or profile:<profile name>.");
    }

    // Every section of the file, as scope -> key -> value. An unknown section or key is an error rather
    // than being ignored, so a typo doesn't silently stop a preference from applying.
    static Dictionary<string, Dictionary<string, string>> ReadProfileSettings(Config config)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string path = SettingsReadPath(config, AppIdentity.ProfileConfigFileName);
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
                if (!sections.TryGetValue(scope, out current))
                    sections[scope] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int equals = text.IndexOf('=');
            if (current == null || equals <= 0) throw new IOException("Invalid profile settings line: " + text);
            Setting setting = FindPreference(text.Substring(0, equals).Trim());
            if (setting == null) throw new IOException("Unsupported profile preference: " + text.Substring(0, equals).Trim());
            current[setting.Key] = text.Substring(equals + 1).Trim().Trim('"');
        }
        return sections;
    }

    // The settings for one device and profile: the global ones with that device's and then that
    // profile's overrides applied. The global settings themselves are never changed.
    internal static Config ResolveProfileSettings(Config global, string device, string profileName)
    {
        var settings = ReadProfileSettings(global);
        Config result = global.Clone();
        foreach (string scope in new[] { "device:" + device, "profile:" + profileName })
        {
            Dictionary<string, string> values;
            if (settings.TryGetValue(scope, out values))
                foreach (var entry in values) ApplySetting(result, entry.Key, entry.Value);
        }
        return result;
    }

    // The settings for a source, once its device is known (the current device).
    static Config ResolveSourceSettings(Config global, string source)
    {
        return ResolveProfileSettings(global, CurrentDevice.Id, ProfileId(source, global.Profile));
    }

    // Replaces one scope's overrides (none removes the scope), keeping the other scopes. Values are
    // checked first, and a line break is refused so a value can't start a new section.
    static void SaveProfileSettings(Config global, string scope, Dictionary<string, string> values)
    {
        ValidateSettingsScope(scope);
        var checkedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in values)
        {
            Setting setting = FindPreference(entry.Key);
            if (setting == null || entry.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("Invalid profile preference.");
            string expected = CheckSettingValue(setting, entry.Value);
            if (expected != null) throw new ArgumentException(setting.Label + " must be " + expected + ".");
            checkedValues[setting.Key] = entry.Value;
        }
        string path = ProfileSettingsPath(global);
        RejectRootLinks(path);
        using (LockHistory(Path.GetDirectoryName(path)))
        {
            var sections = ReadProfileSettings(global);
            if (checkedValues.Count == 0) sections.Remove(scope);
            else sections[scope] = checkedValues;
            var text = new StringBuilder("; Gear Backup preferences: global -> device -> profile.\r\n; Omitted keys inherit their defaults.\r\n");
            var scopes = new List<string>(sections.Keys);
            scopes.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string name in scopes)
            {
                text.AppendLine().Append('[').Append(name).AppendLine("]");
                foreach (Setting setting in Settings)
                {
                    string value;
                    if (setting.IsPreference && sections[name].TryGetValue(setting.Key, out value))
                        text.Append(setting.Key).Append('=').AppendLine(value);
                }
            }
            AtomicText(path, text.ToString());
        }
    }

    // The global backup location plus every other location named in the overrides, so the dashboard and
    // checks can find histories wherever a device or profile stores them.
    internal static List<string> ConfiguredBackupLocations(Config config)
    {
        var locations = new List<string> { config.BackupFolder };
        foreach (var section in ReadProfileSettings(config).Values)
        {
            string value;
            if (!section.TryGetValue("BackupFolder", out value) || string.IsNullOrWhiteSpace(value)) continue;
            string path = ExpandPath(value);
            if (!locations.Exists(p => Rooted(p) == Rooted(path))) locations.Add(path);
        }
        return locations;
    }
}
