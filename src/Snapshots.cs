using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

partial class M8Backup
{
    class RestoreEntry
    {
        public string Relative, StoredPath, Hash;
        public long Size, ModifiedTicks, CreatedTicks;
    }
    class Snapshot
    {
        public string Name;
        public List<RestoreEntry> Files = new List<RestoreEntry>();
        public List<string> Folders = new List<string>();
    }

    static bool ValidHash(string hash) { return hash != null && Regex.IsMatch(hash, "^[a-f0-9]{64}$"); }
    static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
    static string Decode(string value) { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
    static string SnapshotRoot(string backup) { return Path.Combine(backup, ".m8backup", "snapshots"); }
    static string ObjectPath(string backup, string hash)
    {
        if (!ValidHash(hash)) throw new IOException("Invalid snapshot checksum.");
        return SafePath(Path.Combine(backup, ".m8backup", "objects"), hash);
    }

    // Immutable content objects make old manifests independent of renamed/pruned legacy versions.
    static void SaveSnapshot(string backup, string source, ScanResult scan, Dictionary<string, string> sums, DateTime started)
    {
        var rows = new StringBuilder("M8SNAPSHOT\t1\n");
        foreach (string folder in scan.Folders)
            rows.Append("D\t").Append(Encode(folder.Substring(source.Length))).Append('\n');
        foreach (FileInfo original in scan.Files)
        {
            string relative = original.FullName.Substring(source.Length);
            string path = SafePath(backup, relative);
            string hash;
            if (!sums.TryGetValue(relative, out hash) || !ValidHash(hash))
                throw new IOException("No trustworthy checksum for " + relative);
            string obj = ObjectPath(backup, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(obj));
            // Read back even when Verify=no: history must not silently bless a damaged backup.
            if (File.Exists(obj))
            {
                if (HashFile(obj) != hash) throw new IOException("Damaged history object for " + relative);
            }
            else
            {
                string temp = obj + ".tmp";
                TryDelete(temp);
                try
                {
                    if (CopyFile(new FileInfo(path), temp, true) != hash)
                        throw new IOException("Backup checksum differs for " + relative);
                    File.Move(temp, obj);
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
        Console.WriteLine("  Point-in-time snapshot saved: " + Path.GetFileNameWithoutExtension(name));
    }

    static Snapshot ReadSnapshot(string backup, string manifest)
    {
        string[] rows = File.ReadAllLines(manifest, Encoding.UTF8);
        if (rows.Length == 0 || rows[0] != "M8SNAPSHOT\t1") throw new IOException("Unsupported snapshot format.");
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
                long size = long.Parse(parts[3], CultureInfo.InvariantCulture);
                long modified = long.Parse(parts[4], CultureInfo.InvariantCulture);
                long created = long.Parse(parts[5], CultureInfo.InvariantCulture);
                if (size < 0 || modified < 0 || modified > DateTime.MaxValue.Ticks || created < 0 || created > DateTime.MaxValue.Ticks)
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

    static Snapshot ChooseSnapshot(string backup)
    {
        string root = SnapshotRoot(backup);
        if (!Directory.Exists(root)) return null;
        var files = new List<string>(Directory.GetFiles(root, "*.manifest"));
        files.Sort(StringComparer.OrdinalIgnoreCase);
        files.Reverse();
        if (files.Count == 0) return null;
        Console.WriteLine("  Saved points in time:");
        for (int i = 0; i < files.Count; i++) Console.WriteLine("    {0}) {1}", i + 1, Path.GetFileNameWithoutExtension(files[i]));
        int pick = AskNumber("  Choose snapshot (Enter = browse current backup files): ", files.Count);
        return pick == 0 ? null : ReadSnapshot(backup, files[pick - 1]);
    }

    static Snapshot CurrentBackup(string backup)
    {
        var result = new Snapshot { Name = "current backup files" };
        Dictionary<string, string> sums = LoadChecksums(backup);
        foreach (string path in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
        {
            if (IsBackupMetaFile(path)) continue;
            string relative = path.Substring(backup.Length).TrimStart('\\');
            SafePath(backup, relative);
            string hash = null;
            if (sums != null) sums.TryGetValue(relative, out hash);
            var info = new FileInfo(path);
            result.Files.Add(new RestoreEntry { Relative = relative, StoredPath = path, Hash = hash,
                Size = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks, CreatedTicks = info.CreationTimeUtc.Ticks });
        }
        foreach (string folder in Directory.GetDirectories(backup, "*", SearchOption.AllDirectories))
            if (!IsBackupMetaFile(folder)) result.Folders.Add(folder.Substring(backup.Length).TrimStart('\\'));
        return result;
    }

    static void CheckSnapshots(string backup, ref int ok, ref int damaged, ref int missing)
    {
        string root = SnapshotRoot(backup);
        if (!Directory.Exists(root)) return;
        var checkedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifest in Directory.GetFiles(root, "*.manifest"))
        {
            try
            {
                Snapshot snapshot = ReadSnapshot(backup, manifest);
                foreach (RestoreEntry item in snapshot.Files)
                {
                    if (!checkedObjects.Add(item.Hash)) continue;
                    if (!File.Exists(item.StoredPath)) { missing++; Console.WriteLine("    Missing history object: " + item.Hash); }
                    else
                    {
                        try { ValidateEntry(item); ok++; }
                        catch (Exception ex) { damaged++; Console.WriteLine("    Damaged history: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { damaged++; Console.WriteLine("    Invalid snapshot: " + ex.Message); }
        }
        Console.WriteLine("    {0} unique history object(s) checked.", checkedObjects.Count);
    }
}
