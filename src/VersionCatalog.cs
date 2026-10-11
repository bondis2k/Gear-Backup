using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static PathSafety;
using static BackupTraversal;
using static Checksums;
using static Snapshots;
using static Backups;
using static Workflow;

// Which dated files in a backup are old versions the program kept (Song_2026-10-01_143000.m8s), as
// opposed to the user's own files that happen to have a date in their name. Storage cleanup only offers
// known old versions.
static class VersionCatalog
{
    // The backup's known old versions, from .gearbackup\old-versions.txt. Backups made before that record
    // existed fall back to their snapshots: a dated name never seen on the source is an old version.
    // Without either, nothing is reported, since a date in a name alone isn't evidence enough.
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
        // Older backups with snapshots can identify names that never appeared on the source.
        // Without that evidence, individual versions are left out of cleanup.
        string snapshots = SnapshotRoot(backup);
        if (!Directory.Exists(snapshots)) return result;
        string[] manifests = Directory.GetFiles(snapshots, "*.manifest");
        if (manifests.Length == 0) return result;
        // Only dated names can be old versions; most backups have none, and then the manifests, which
        // can list many thousands of files, needn't be read at all.
        var dated = new List<string>();
        foreach (string file in BackupFiles(backup))
        {
            string name = file.Substring(Rooted(backup).Length);
            if (IsOldVersion(name)) dated.Add(name);
        }
        if (dated.Count == 0) return result;
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifest in manifests) sourceNames.UnionWith(SnapshotFileNames(manifest));
        foreach (string name in dated)
            if (!sourceNames.Contains(name)) result.Add(name);
        return result;
    }

    // The file names a manifest lists, for comparing names only. Unlike ReadSnapshot it doesn't check each
    // path on disk (nothing is read or written through these names), which makes it many times faster;
    // the format is still checked, so a damaged manifest is reported rather than half-read.
    internal static List<string> SnapshotFileNames(string manifest)
    {
        string[] rows = Records.ReadLines(manifest, Records.ListLimit, "Snapshot manifest");
        if (rows.Length == 0 || (rows[0] != AppIdentity.SnapshotHeader && rows[0] != AppIdentity.Legacy.SnapshotHeader))
            throw new IOException("Unsupported snapshot format.");
        var names = new List<string>();
        for (int i = 1; i < rows.Length; i++)
        {
            string[] parts = rows[i].Split('\t');
            if (parts[0] == "F" && parts.Length == 6) names.Add(Decode(parts[1]).Replace('/', '\\'));
            else if (parts[0] != "D" || parts.Length != 2) throw new IOException("Invalid snapshot row.");
        }
        return names;
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
