using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static PathSafety;
using static Checksums;
using static Backups;
using static Workflow;

// Which dated files in a backup are old versions the program kept (Song_2026-10-01_143000.m8s), as
// opposed to the user's own files that happen to have a date in their name. Storage cleanup only offers
// known old versions.
static class VersionCatalog
{
    // The backup's known old versions, from .gearbackup\old-versions.txt. A date in a source
    // filename is never evidence of a retained version; only this explicit record counts.
    internal static HashSet<string> KnownOldVersions(string backup)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "old-versions.txt");
        if (File.Exists(path))
        {
            string[] lines = Records.ReadLines(path, Records.OldVersionLimit, "Old-version record");
            if (lines.Length == 0 || lines[0] != "GEARVERSIONS\t1") throw new IOException("Invalid old-version record.");
            for (int i = 1; i < lines.Length; i++)
            {
                string name;
                try { name = Encoding.UTF8.GetString(Convert.FromBase64String(lines[i])); }
                catch (FormatException ex) { throw new IOException("Invalid old-version path.", ex); }
                string full = SafePath(backup, name);
                if (!IsOldVersion(name) || IsBackupMetaFile(full)) throw new IOException("Invalid old-version entry.");
                if (File.Exists(full)) result.Add(name);
            }
            return result;
        }
        return result;
    }

    // Updates the record after a backup: adds the versions it kept, and drops names that are on the source
    // again or no longer exist.
    internal static void SaveOldVersionRecord(string backup, HashSet<string> versions, List<string> added, HashSet<string> sourceNames)
    {
        foreach (string name in added) if (!IsBackupMetaFile(Path.Combine(backup, name))) versions.Add(name);
        versions.RemoveWhere(name => sourceNames.Contains(name) || !File.Exists(SafePath(backup, name)));
        var names = new List<string>(versions);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder("GEARVERSIONS\t1\n");
        foreach (string name in names) text.AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(name)));
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "old-versions.txt");
        AtomicText(path, text.ToString());
    }
}
