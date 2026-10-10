using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Which dated files in a backup are old versions the program kept (Song_2026-10-01_143000.m8s), as
// opposed to the user's own files that happen to have a date in their name. Storage cleanup only offers
// known old versions.
partial class GearBackup
{
    // The backup's known old versions, from .gearbackup\old-versions.txt. Backups made before that record
    // existed fall back to their snapshots: a dated name never seen on the source is an old version.
    // Without either, nothing is reported, since a date in a name alone isn't evidence enough.
    static HashSet<string> KnownOldVersions(string backup)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "old-versions.txt");
        if (File.Exists(path))
        {
            RejectRootLinks(path);
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Old-version record is too large.");
            string[] lines = File.ReadAllLines(path);
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
        // Older backups with snapshots can identify names that never appeared on the source.
        // Without that evidence, individual versions are left out of cleanup.
        string snapshots = SnapshotRoot(backup);
        if (!Directory.Exists(snapshots)) return result;
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] manifests = Directory.GetFiles(snapshots, "*.manifest");
        if (manifests.Length == 0) return result;
        foreach (string manifest in manifests)
            foreach (RestoreEntry entry in ReadSnapshot(backup, manifest).Files) sourceNames.Add(entry.Relative);
        foreach (string file in BackupFiles(backup))
        {
            string name = file.Substring(Rooted(backup).Length);
            if (IsOldVersion(name) && !sourceNames.Contains(name)) result.Add(name);
        }
        return result;
    }

    // Updates the record after a backup: adds the versions it kept, and drops names that are on the source
    // again or no longer exist.
    static void SaveOldVersionRecord(string backup, HashSet<string> versions, List<string> added, HashSet<string> sourceNames)
    {
        foreach (string name in added) if (!IsBackupMetaFile(Path.Combine(backup, name))) versions.Add(name);
        versions.RemoveWhere(name => sourceNames.Contains(name) || !File.Exists(SafePath(backup, name)));
        var names = new List<string>(versions);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder("GEARVERSIONS\t1\n");
        foreach (string name in names) text.AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(name)));
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "old-versions.txt");
        RejectRootLinks(path);
        AtomicText(path, text.ToString());
    }
}
