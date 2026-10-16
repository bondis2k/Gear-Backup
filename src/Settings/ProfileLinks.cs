using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GearBackup
{
    // Associations live with the application's settings, never on either card.
    internal static class ProfileLinks
    {
        const string FileName = "GearBackup.CardLinks.ini";

        internal static Dictionary<string, string> Read(Config config)
        {
            var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = SettingsLocation.SettingsReadPath(config, FileName);
            PathSafety.RejectRootLinks(path);
            if (!File.Exists(path)) return links;
            foreach (string line in Records.ReadLines(path, Records.SettingsLimit, "Card links"))
            {
                if (line.Length == 0 || line.StartsWith(";")) continue;
                int split = line.IndexOf('=');
                if (split < 1) throw new IOException("Invalid card history link.");
                string key = line.Substring(0, split), history = line.Substring(split + 1);
                ProfileNaming.ValidateProfile(key);
                ValidateHistory(history);
                links.Add(key, history);
            }
            return links;
        }

        static void ValidateHistory(string history)
        {
            if (!Path.IsPathRooted(history) || history.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(history)), "profiles", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(history))), AppIdentity.BackupsFolderName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid linked history location.");
            ProfileNaming.ValidateProfile(Path.GetFileName(history));
        }

        internal static string Find(Config config, string source)
        {
            if (config.Profile.Length != 0 || DeviceSelection.CurrentDevice.Id != Devices.M8.Id) return null;
            string history;
            return Read(config).TryGetValue("m8-" + ProfileNaming.SourceKey(PathSafety.Rooted(source)), out history) ? history : null;
        }

        internal static string Key(string target)
        {
            string normalized = PathSafety.Rooted(target);
            string key = ProfileNaming.SourceKey(normalized);
            if (normalized != PathSafety.Rooted(Path.GetPathRoot(normalized)) || !key.StartsWith("card-"))
                throw new IOException("Linking requires a replacement card's drive root with a readable volume ID. Leave linking unchecked for a test folder.");
            return "m8-" + key;
        }

        internal static void ValidateLink(Config config, string key, string history)
        {
            ProfileNaming.ValidateProfile(key); ValidateHistory(history);
            string existing;
            if (Read(config).TryGetValue(key, out existing) && PathSafety.Rooted(existing) != PathSafety.Rooted(history))
                throw new IOException("This card is already linked to another backup history.");
        }

        internal static void Save(Config config, string key, string history)
        {
            string path = SettingsLocation.SettingsReadPath(config, FileName);
            PathSafety.RejectRootLinks(path);
            using (HistoryLock.LockHistory(Path.GetDirectoryName(path)))
            {
                ValidateLink(config, key, history);
                var links = Read(config); links[key] = Path.GetFullPath(history);
                var text = new StringBuilder("; Replacement M8 card volume IDs and their original backup histories.\r\n");
                var keys = new List<string>(links.Keys); keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in keys) text.Append(name).Append('=').AppendLine(links[name]);
                AtomicFile.AtomicText(path, text.ToString());
            }
        }
    }
}
