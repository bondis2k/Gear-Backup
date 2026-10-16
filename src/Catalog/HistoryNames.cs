using System;
using System.IO;
using static GearBackup.AtomicFile;

namespace GearBackup
{
    // Readable names for backup histories (the folders under Gear Backups\profiles). A history's folder is
    // named after its device and card ("m8-card-DCE92C2E"), its device's serial for MTP devices, or a
    // backup name typed by the user ("Studio"); those folder names stay as they are on disk. Windows and
    // the CLI show them as "M8 · card DCE92C2E", "X100VI · photos" and so on, or as a nickname the user
    // gave the history, which is kept inside the history folder (.gearbackup\nickname.txt) so it travels
    // with the backups.
    static class HistoryNames
    {
        internal const int MaxNickname = 64;
        const string NicknameFile = "nickname.txt";

        // The name to show for a history: its nickname, or the readable automatic name.
        internal static string DisplayName(string history)
        {
            return Nickname(history) ?? AutomaticName(Path.GetFileName(history.TrimEnd('\\')));
        }

        // "m8-card-DCE92C2E" -> "M8 · card DCE92C2E"; "x100vi-<serial>" -> "X100VI · photos";
        // "x100vi-settings-<serial>" -> "X100VI · settings"; "m8-folder-<hash>" -> "M8 · folder 1a2b".
        // A typed backup name (no device prefix) is shown as it is.
        internal static string AutomaticName(string folderName)
        {
            DeviceProfile match = null;
            foreach (DeviceProfile device in Devices.All)
                if (folderName.StartsWith(device.Id + "-", StringComparison.OrdinalIgnoreCase) && (match == null || device.Id.Length > match.Id.Length))
                    match = device;
            if (match == null) return folderName;
            string rest = folderName.Substring(match.Id.Length + 1);
            if (rest.StartsWith("settings-", StringComparison.OrdinalIgnoreCase)) return match.Name + " · settings";
            if (rest.StartsWith("card-", StringComparison.OrdinalIgnoreCase)) return match.Name + " · card " + rest.Substring(5);
            if (rest.StartsWith("folder-", StringComparison.OrdinalIgnoreCase))
                return match.Name + " · folder " + rest.Substring(7, Math.Min(4, rest.Length - 7));
            // Only MTP devices' histories are named after the device's serial; anything else is a typed name.
            if (!match.Mtp) return folderName;
            if (match == Devices.X100VI) return match.Name + " · photos";
            return match.Name + " · " + rest;
        }

        // The history's nickname, or null when it has none (or it can't be read).
        internal static string Nickname(string history)
        {
            try
            {
                string path = NicknamePath(history);
                if (!File.Exists(path)) return null;
                string name = Records.ReadText(path, Records.SmallLimit, "History nickname").Trim();
                return Valid(name) ? name : null;
            }
            catch (Exception) { return null; }  // An unreadable nickname only falls back to the automatic name.
        }

        // Gives a history a nickname; an empty one goes back to the automatic name.
        internal static void SetNickname(string history, string name)
        {
            name = (name ?? "").Trim();
            if (!Directory.Exists(history)) throw new IOException("The backup history no longer exists: " + history);
            string path = NicknamePath(history);
            if (name.Length == 0)
            {
                if (File.Exists(path)) { PathSafety.RejectRootLinks(path); File.Delete(path); }
                return;
            }
            if (!Valid(name)) throw new ArgumentException("Use up to " + MaxNickname + " characters, without line breaks.");
            AtomicText(path, name + "\n");
        }

        static bool Valid(string name)
        {
            if (name.Length == 0 || name.Length > MaxNickname) return false;
            foreach (char c in name) if (char.IsControl(c)) return false;
            return true;
        }

        static string NicknamePath(string history) { return Path.Combine(AppIdentity.MetadataRoot(history), NicknameFile); }
    }

}
