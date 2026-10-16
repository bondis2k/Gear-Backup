// Song names for a device, and the check for file names the device may not handle well.
using System;
using System.Collections.Generic;
using System.IO;
using static GearBackup.DeviceSelection;
using static GearBackup.ChangeSummary;

namespace GearBackup
{
    static class ChangeSummary
    {
        internal static bool IsSong(string path)
        {
            return CurrentDevice.IsSong(path);
        }

        // "Songs\Live\Jam.m8s" -> "Live\Jam" (the device's songs folder is left out). Where each song is a
        // folder: "Projects\User\Jam\project.mt" -> "User\Jam".
        internal static string SongName(string relative)
        {
            string name = CurrentDevice.SongIsFolder ? Path.GetDirectoryName(relative)
                : Path.Combine(Path.GetDirectoryName(relative), Path.GetFileNameWithoutExtension(relative));
            string folder = CurrentDevice.SongsFolder == null ? null : CurrentDevice.SongsFolder + "\\";
            return folder != null && name.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? name.Substring(folder.Length) : name;
        }

        // Flags names the M8 may not show or load properly: characters outside plain ASCII, very long names,
        // and names starting or ending with a space.
        internal static List<string> CheckNames(ScanResult scan, string source, int maxLength)
        {
            var paths = new List<string>(scan.Folders);
            foreach (FileInfo f in scan.Files) paths.Add(f.FullName);

            var warnings = new List<string>();
            foreach (string path in paths)
            {
                string name = Path.GetFileName(path);
                var problems = new List<string>();
                foreach (char c in name)
                    if (c < 32 || c > 126) { problems.Add("characters other than plain letters, digits and symbols"); break; }
                if (maxLength > 0 && name.Length > maxLength)
                    problems.Add("name is " + name.Length + " characters long (MaxNameLength is " + maxLength + ")");
                if (name != name.Trim())
                    problems.Add("starts or ends with a space");
                if (problems.Count > 0)
                    warnings.Add(path.Substring(source.Length) + ": " + string.Join(", ", problems.ToArray()));
            }
            return warnings;
        }
    }

}
