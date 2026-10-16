using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using static GearBackup.PathSafety;

namespace GearBackup
{
    // Display/file names are short; capture times remain independent metadata for chronological ordering.
    internal static class RestorePointNames
    {
        internal static string Create(string backup, DateTime started, bool incremental)
        {
            string root = Snapshots.SnapshotRoot(backup);
            string name = started.ToString(incremental ? "yyyy-MM-dd_HH.mm.ss" : "yyyy-MM-dd_HHmmss_fffffff", CultureInfo.InvariantCulture);
            string candidate = name;
            for (int suffix = 2; ; suffix++)
            {
                bool used = false;
                foreach (string extension in new[] { ".manifest", ".kind", ".taken" })
                {
                    string path = SafePath(root, candidate + extension);
                    if (File.Exists(path) || Directory.Exists(path)) used = true;
                }
                if (!used) return candidate;
                candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            }
        }

        internal static void SaveMetadata(string backup, string name, DateTime started, bool incremental, string customName = null)
        {
            string root = Snapshots.SnapshotRoot(backup);
            AtomicFile.AtomicText(SafePath(root, name + ".kind"), incremental ? "incremental" : "full");
            AtomicFile.AtomicText(SafePath(root, name + ".taken"), started.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            customName = BackupSetNames.Validate(customName);
            if (customName.Length != 0) AtomicFile.AtomicText(SafePath(root, name + ".name"), customName);
        }

        // The name the user gave a restore point (shown beside its date and time), or null when it has none.
        internal static string ReadName(string backup, string name)
        {
            string path = SafePath(Snapshots.SnapshotRoot(backup), name + ".name");
            if (!File.Exists(path)) return null;
            try
            {
                string text = BackupSetNames.Validate(Records.ReadText(path, Records.SmallLimit, "Restore point name"));
                return text.Length == 0 ? null : text;
            }
            catch (ArgumentException) { return null; } // an unusable name only falls back to the date and time
        }

        internal static DateTime TakenUtc(string backup, string name)
        {
            DateTime time;
            string text = Records.ReadText(SafePath(Snapshots.SnapshotRoot(backup), name + ".taken"), Records.SmallLimit, "Restore point capture time").Trim();
            if (!DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time) || time.Kind != DateTimeKind.Utc)
                throw new IOException("Invalid restore point capture time.");
            return time;
        }

        internal static List<string> OrderedManifests(string backup)
        {
            string root = Snapshots.SnapshotRoot(backup);
            RejectRootLinks(root);
            var files = new List<string>(Directory.GetFiles(root, "*.manifest"));
            var times = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (string file in files) times.Add(file, TakenUtc(backup, Path.GetFileNameWithoutExtension(file)));
            files.Sort((a, b) => { int order = times[b].CompareTo(times[a]); return order != 0 ? order : CompareNames(Path.GetFileNameWithoutExtension(b), Path.GetFileNameWithoutExtension(a)); });
            return files;
        }

        internal static int CompareNames(string a, string b)
        {
            string baseA, baseB;
            int suffixA = Suffix(a, out baseA), suffixB = Suffix(b, out baseB);
            int order = StringComparer.Ordinal.Compare(baseA, baseB);
            return order != 0 ? order : suffixA.CompareTo(suffixB);
        }
        static int Suffix(string name, out string basis)
        {
            int index = name.LastIndexOf('_'), suffix;
            if ((index == 19 || index == 25) && int.TryParse(name.Substring(index + 1), NumberStyles.None, CultureInfo.InvariantCulture, out suffix))
            { basis = name.Substring(0, index); return suffix; }
            basis = name; return 1;
        }
    }

}
