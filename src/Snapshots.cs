using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

partial class GearBackup
{
    // One file that can be restored: where it goes (Relative), where its content is (StoredPath), and
    // the size, times and checksum it had when backed up. Hash is null for older backups without one.
    internal class RestoreEntry
    {
        public string Relative, StoredPath, Hash;
        public long Size, ModifiedTicks, CreatedTicks;
    }
    // The files and folders of one point in time, from a manifest or from the current backup files.
    internal class Snapshot
    {
        public string Name;
        public List<RestoreEntry> Files = new List<RestoreEntry>();
        public List<string> Folders = new List<string>();
    }

    static bool ValidHash(string hash) { return hash != null && Regex.IsMatch(hash, "^[a-f0-9]{64}$"); }
    // Paths are stored Base64-encoded in manifests, so tabs or other odd characters in a name can't
    // break the tab-separated format.
    static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
    static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException ex) { throw new IOException("Invalid encoded snapshot path.", ex); }
    }
    static string SnapshotRoot(string backup) { return Path.Combine(AppIdentity.MetadataRoot(backup), "snapshots"); }
    static string ObjectPath(string backup, string hash)
    {
        if (!ValidHash(hash)) throw new IOException("Invalid snapshot checksum.");
        return SafePath(Path.Combine(AppIdentity.MetadataRoot(backup), "objects"), hash);
    }

    // Immutable content objects make old manifests independent of renamed/pruned legacy versions.
    // Manifest format (.gearbackup\snapshots\<time>.manifest), one entry per line, tab-separated:
    //   GEARSNAPSHOT  1                                        header and format version
    //   D  <path>                                            a folder (kept even when empty)
    //   F  <path>  <sha256>  <size>  <modified>  <created>   a file; times are UTC ticks
    // The file's content is stored once in .gearbackup\objects\<sha256>.
    static void SaveSnapshot(string backup, string source, ScanResult scan, Dictionary<string, string> sums, DateTime started, bool verify, bool hardLinks)
    {
        var rows = new StringBuilder(AppIdentity.SnapshotHeader + "\n");
        var checkedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in scan.Folders)
            rows.Append("D\t").Append(Encode(folder.Substring(source.Length))).Append('\n');
        foreach (FileInfo original in scan.Files)
        {
            OperationContext.Current.Progress(Stage.SavingHistory, original.FullName, 0, 0);
            string relative = original.FullName.Substring(source.Length);
            string path = SafePath(backup, relative);
            string hash;
            if (!sums.TryGetValue(relative, out hash) || !ValidHash(hash))
                throw new IOException("No trustworthy checksum for " + relative);
            string obj = ObjectPath(backup, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(obj));
            // Optional integrity checks are independent of generating object identities.
            if (File.Exists(obj))
            {
                if (verify && checkedObjects.Add(hash) && HashFile(obj) != hash) throw new IOException("Damaged history object for " + relative);
            }
            else
            {
                string temp = obj + ".tmp";
                TryDelete(temp);
                try
                {
                    // HardLinks=yes: a hard link stores the content once (NTFS); otherwise, or where linking
                    // fails, a copy. The program never edits backup files in place, so linked content stays as recorded.
                    string storedHash = hardLinks && TryHardLink(path, temp)
                        ? (verify ? HashFile(temp) : hash)
                        : CopyFile(new FileInfo(path), temp, verify);
                    if (verify && storedHash != hash)
                        throw new IOException("Backup checksum differs for " + relative);
                    File.Move(temp, obj);
                    checkedObjects.Add(hash);
                }
                finally { TryDelete(temp); }
            }
            var info = new FileInfo(path);
            rows.Append("F\t").Append(Encode(relative)).Append('\t').Append(hash).Append('\t')
                .Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(original.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(original.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        string name = started.ToString("yyyy-MM-dd_HHmmss_fffffff", CultureInfo.InvariantCulture) + ".manifest";
        AtomicText(Path.Combine(SnapshotRoot(backup), name), rows.ToString());
        OperationOutput.WriteLine("  Point-in-time snapshot saved: " + Path.GetFileNameWithoutExtension(name));
    }

    // Reads and validates a manifest. A damaged or tampered manifest must not be able to write outside
    // the target, so paths are checked, duplicates rejected, and no file may sit where a folder must go.
    static Snapshot ReadSnapshot(string backup, string manifest)
    {
        string[] rows = File.ReadAllLines(manifest, Encoding.UTF8);
        if (rows.Length == 0 || (rows[0] != AppIdentity.SnapshotHeader && rows[0] != AppIdentity.Legacy.SnapshotHeader))
            throw new IOException("Unsupported snapshot format.");
        var result = new Snapshot { Name = Path.GetFileNameWithoutExtension(manifest) };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < rows.Length; i++)
        {
            string[] parts = rows[i].Split('\t');
            if (parts.Length < 2) throw new IOException("Invalid snapshot row.");
            string relative = Decode(parts[1]).Replace('/', '\\');
            SafePath(backup, relative);
            if (!names.Add(relative)) throw new IOException("Duplicate snapshot path: " + relative);
            if (parts[0] == "D" && parts.Length == 2) result.Folders.Add(relative);
            else if (parts[0] == "F" && parts.Length == 6)
            {
                long size, modified, created;
                if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
                    !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out modified) ||
                    !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out created) ||
                    size < 0 || modified < 0 || modified > DateTime.MaxValue.Ticks || created < 0 || created > DateTime.MaxValue.Ticks)
                    throw new IOException("Invalid snapshot metadata.");
                result.Files.Add(new RestoreEntry { Relative = relative, Hash = parts[2], Size = size,
                    ModifiedTicks = modified, CreatedTicks = created, StoredPath = ObjectPath(backup, parts[2]) });
            }
            else throw new IOException("Invalid snapshot row.");
        }
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RestoreEntry entry in result.Files) fileNames.Add(entry.Relative);
        foreach (string name in names)
        {
            string parent = Path.GetDirectoryName(name);
            while (!string.IsNullOrEmpty(parent))
            {
                if (fileNames.Contains(parent)) throw new IOException("Snapshot file blocks a parent folder: " + parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        return result;
    }

    // Lets the user pick a saved point in time, newest first. Null means "use the current backup files".
    static Snapshot ChooseSnapshot(string backup)
    {
        string root = SnapshotRoot(backup);
        if (!Directory.Exists(root)) return null;
        var files = new List<string>(Directory.GetFiles(root, "*.manifest"));
        files.Sort(StringComparer.OrdinalIgnoreCase);
        files.Reverse();
        if (files.Count == 0) return null;
        var names = new List<string>();
        foreach (string file in files) names.Add(Path.GetFileNameWithoutExtension(file));
        int pick = AskItem("Restore from which saved point in time?", names, "Current backup files (no snapshot)");
        return pick == 0 ? null : ReadSnapshot(backup, files[pick - 1]);
    }

    // The backup folder's files as they are now, as a snapshot. Checksums come from checksums.sha256
    // where available.
    static Snapshot CurrentBackup(string backup)
    {
        var result = new Snapshot { Name = "current backup files" };
        Dictionary<string, string> sums = LoadChecksums(backup);
        foreach (FileSystemInfo entry in BackupEntries(backup))
        {
            if (entry is DirectoryInfo)
            {
                result.Folders.Add(entry.FullName.Substring(backup.Length).TrimStart('\\'));
                continue;
            }
            string path = entry.FullName;
            string relative = path.Substring(backup.Length).TrimStart('\\');
            SafePath(backup, relative);
            string hash = null;
            if (sums != null) sums.TryGetValue(relative, out hash);
            var info = new FileInfo(path);
            result.Files.Add(new RestoreEntry { Relative = relative, StoredPath = path, Hash = hash,
                Size = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks, CreatedTicks = info.CreationTimeUtc.Ticks });
        }
        return result;
    }

    // HardLinks=yes: the object is the same file on disk as a backup file just checked with this checksum.
    static bool AlreadyVerified(RestoreEntry item, Dictionary<string, string> verifiedLinks)
    {
        if (verifiedLinks == null || verifiedLinks.Count == 0) return false;
        string id = SharedFileId(item.StoredPath), hash;
        return id != null && verifiedLinks.TryGetValue(id, out hash) && hash == item.Hash;
    }

    // Checks every history object referenced by any manifest in this backup; each object once.
    static void CheckSnapshots(string backup, ref int ok, ref int damaged, ref int missing,
                               Dictionary<string, string> verifiedLinks = null)
    {
        string root = SnapshotRoot(backup);
        if (!Directory.Exists(root)) return;
        var checkedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifest in Directory.GetFiles(root, "*.manifest"))
        {
            OperationContext.Current.Progress(Stage.CheckingHistory, manifest, 0, 0);
            try
            {
                Snapshot snapshot = ReadSnapshot(backup, manifest);
                foreach (RestoreEntry item in snapshot.Files)
                {
                    if (!checkedObjects.Add(item.Hash)) continue;
                    if (!File.Exists(item.StoredPath)) { missing++; OperationOutput.WriteLine("    Missing history object: " + item.Hash); }
                    else if (AlreadyVerified(item, verifiedLinks)) ok++;
                    else
                    {
                        try { ValidateEntry(item); ok++; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { damaged++; OperationOutput.WriteLine("    Damaged history: " + ex.Message); }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { damaged++; OperationOutput.WriteLine("    Invalid snapshot: " + ex.Message); }
        }
        OperationOutput.WriteLine("    {0} unique history object(s) checked.", checkedObjects.Count);
    }
}
