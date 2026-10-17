using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static GearBackup.FileCopy;
using static GearBackup.PathSafety;
using static GearBackup.MtpMirror;
using static GearBackup.DeviceSelection;

namespace GearBackup
{
    // How a source is named in the backup folders: which history (profile) it belongs to.
    static class ProfileNaming
    {
        // Profile names become folder names, so they're limited to safe characters.
        internal static void ValidateProfile(string name)
        {
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$") || name.EndsWith(" ") ||
                Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                throw new ArgumentException("Profile must be 1-64 letters, digits, spaces, - or _, and not a Windows reserved name.");
        }

        // The history folder name for a source, starting with the current device's id so each device's
        // histories are easy to tell apart. A whole drive is identified by its volume serial number
        // ("m8-card-1A2B3C4D"), so the same SD card keeps its history in another reader or drive letter. A
        // folder gets a short hash of its full path ("m8-folder-..."). A mirrored MTP device is named by its
        // mirror folder ("op1-field-L1PRC121"). A named Profile setting overrides all of these.
        internal static string ProfileId(string source, string requested, DeviceProfile device = null)
        {
            if (requested.Length > 0) { ValidateProfile(requested); return requested; }
            string normalized = Rooted(source);
            if (Within(normalized, MirrorRoot) && Rooted(normalized) != Rooted(MirrorRoot))
                return Path.GetFileName(normalized.TrimEnd('\\'));
            return (device ?? CurrentDevice).Id + "-" + SourceKey(normalized);
        }

        // What identifies a drive or folder, without the device: "card-1A2B3C4D" or "folder-<hash>". Before
        // histories were named per device, this alone was the history's name.
        internal static string SourceKey(string normalized)
        {
            if (normalized == Rooted(Path.GetPathRoot(normalized)))
            {
                // Asked of Windows directly: WMI's Win32_LogicalDisk leaves the serial blank for some cards
                // (the Tracker Mini's exFAT card). Same 8 hex digits as WMI.
                uint serial, maxComponent, flags;
                if (GetVolumeInformationW(normalized, null, 0, out serial, out maxComponent, out flags, null, 0) && serial != 0)
                    return "card-" + serial.ToString("X8");
            }
            using (var sha = SHA256.Create())
                return "folder-" + ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()))).Substring(0, 16);
        }

        // Each device/source pair has its own history. A named profile uses the requested name directly.
        internal static string ProfileFolder(string profiles, string source, string requested, DeviceProfile device = null)
        {
            return Path.Combine(profiles, ProfileId(source, requested, device));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool GetVolumeInformationW(string rootPath, StringBuilder volumeName, int volumeNameSize,
            out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, StringBuilder fileSystemName, int fileSystemNameSize);
    }

}
