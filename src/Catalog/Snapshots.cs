using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using static FileCopy;
using static PathSafety;
using static BackupTraversal;
using static Checksums;
using static VersionCatalog;
using static Backups;
using static RestoreFlow;
using static ConsoleUi;
using static AtomicFile;
using static Snapshots;

static class Snapshots
{
    internal static bool ValidHash(string hash)
    {
        if (hash == null || hash.Length != 64) return false;
        foreach (char c in hash) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
    // Paths are stored Base64-encoded in manifests, so tabs or other odd characters in a name can't
    // break the tab-separated format.
    internal static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
    internal static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException ex) { throw new IOException("Invalid encoded snapshot path.", ex); }
    }
    internal static string SnapshotRoot(string backup) { return Path.Combine(AppIdentity.MetadataRoot(backup), "snapshots"); }
    internal static string ObjectPath(string backup, string hash)
    {
        if (!ValidHash(hash)) throw new IOException("Invalid snapshot checksum.");
        return SafePath(Path.Combine(AppIdentity.MetadataRoot(backup), "objects"), hash);
    }

    // Immutable content objects keep saved restore points independent of renamed/pruned file versions.
    // Manifest format (.gearbackup\snapshots\<time>.manifest), one entry per line, tab-separated:
    //   GEARSNAPSHOT  1                                        header and format version
    //   D  <path>                                            a folder (kept even when empty)
    //   F  <path>  <sha256>  <size>  <modified>  <created>   a file; times are UTC ticks
    // The file's content is stored once in .gearbackup\objects\<sha256>.
    internal static void SaveSnapshot(string backup, string source, ScanResult scan, Dictionary<string, string> sums, DateTime started, bool verify, bool hardLinks, bool incremental = false, string customName = null)
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
            // An object that already exists was verified when it was written (with Verify=yes), so here it
            // only has to have the right size: reading every existing object again on each backup took
            // longer than the backup itself once the history grew. Damage that keeps the size is found by
            // Check backups, which reads every object.
            if (File.Exists(obj))
            {
                if (checkedObjects.Add(hash) && new FileInfo(obj).Length != new FileInfo(path).Length)
                    throw new IOException("Damaged history object for " + relative);
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
        string name = RestorePointNames.Create(backup, started, incremental);
        AtomicText(Path.Combine(SnapshotRoot(backup), name + ".manifest"), rows.ToString());
        RestorePointNames.SaveMetadata(backup, name, started, incremental, customName);
        OperationOutput.WriteLine("  Point-in-time snapshot saved: " + name);
    }

    // Reads and validates a manifest. A damaged or tampered manifest must not be able to write outside
    // the target, so paths are checked, duplicates rejected, and no file may sit where a folder must go.
    // `validNames`, shared by the manifests of one backup, remembers names already found valid: the check is a pure
    // string check (CheckedPath never touches the disk) and every restore point of a long history repeats the same names.
    internal static Snapshot ReadSnapshot(string backup, string manifest, ObjectChecks objects = null, HashSet<string> validNames = null)
    {
        // Checked once for the whole manifest (and, when given, shared by every manifest of a backup).
        RejectRootLinks(backup);
        objects = objects ?? new ObjectChecks(backup);
        string[] rows = Records.ReadLines(manifest, Records.ListLimit, "Snapshot manifest");
        if (rows.Length == 0 || rows[0] != AppIdentity.SnapshotHeader)
            throw new IOException("Unsupported snapshot format.");
        var result = new Snapshot { Name = Path.GetFileNameWithoutExtension(manifest) };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < rows.Length; i++)
        {
            string[] parts = rows[i].Split('\t');
            if (parts.Length < 2) throw new IOException("Invalid snapshot row.");
            string relative = Decode(parts[1]).Replace('/', '\\');
            if (validNames == null || !validNames.Contains(relative))
            {
                CheckedPath(backup, relative);
                if (validNames != null) validNames.Add(relative); // only after it passed
            }
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
                    ModifiedTicks = modified, CreatedTicks = created, StoredPath = objects.PathOf(parts[2]) });
                objects.Present(parts[2]); // refuses a linked content object
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
        result.TakenUtc = RestorePointNames.TakenUtc(backup, result.Name);
        result.CustomName = RestorePointNames.ReadName(backup, result.Name);
        return result;
    }

    // Lets the user pick a saved point in time, newest first. Null means "use the current backup files".
    internal static Snapshot ChooseSnapshot(string backup)
    {
        string root = SnapshotRoot(backup);
        if (!Directory.Exists(root)) return null;
        var files = RestorePointNames.OrderedManifests(backup);
        if (files.Count == 0) return null;
        var names = new List<string>();
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string custom = RestorePointNames.ReadName(backup, name);
            names.Add((custom == null ? "" : custom + " · ") + name + " · Backup taken " + RestorePointNames.TakenUtc(backup, name).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        int pick = AskItem("Restore from which saved point in time?", names,
            ContentStoreBackup.IsSingle(backup) ? "Latest restore point" : "Current backup files (no snapshot)");
        return pick == 0 ? null : ReadSnapshot(backup, files[pick - 1]);
    }

    // The backup folder's files as they are now, as a snapshot. Checksums come from checksums.sha256
    // where available.
    internal static Snapshot CurrentBackup(string backup)
    {
        if (ContentStoreBackup.IsSingle(backup)) return ContentStoreBackup.Current(backup);
        var result = new Snapshot { Name = "current backup files", OldVersions = KnownOldVersions(backup) };
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
    internal static void CheckSnapshots(string backup, ref int ok, ref int damaged, ref int missing,
                               Dictionary<string, string> verifiedLinks = null, List<CatalogVerification> catalogResults = null)
    {
        string root = SnapshotRoot(backup);
        if (!Directory.Exists(root)) return;
        var checkedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validNames = new HashSet<string>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifest in Directory.GetFiles(root, "*.manifest"))
        {
            OperationContext.Current.Progress(Stage.CheckingHistory, manifest, 0, 0);
            try
            {
                Snapshot snapshot = ReadSnapshot(backup, manifest, null, validNames);
                foreach (RestoreEntry item in snapshot.Files)
                {
                    if (checkedObjects.Add(item.Hash))
                    {
                        if (!File.Exists(item.StoredPath)) { missing++; outcomes[item.Hash] = "missing"; OperationOutput.WriteLine("    Missing history object: " + item.Hash); }
                        else if (AlreadyVerified(item, verifiedLinks)) { ok++; outcomes[item.Hash] = "ok"; }
                        else
                        {
                            try { ValidateEntry(item); ok++; outcomes[item.Hash] = "ok"; }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) { damaged++; outcomes[item.Hash] = "damaged"; OperationOutput.WriteLine("    Damaged history: " + ex.Message); }
                        }
                    }
                    if (catalogResults != null) catalogResults.Add(new CatalogVerification { SnapshotId = snapshot.Name, Path = item.Relative,
                        ExpectedHash = item.Hash, ObservedHash = outcomes[item.Hash] == "ok" ? item.Hash : null, Result = outcomes[item.Hash] });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { damaged++; OperationOutput.WriteLine("    Invalid snapshot: " + ex.Message); }
        }
        OperationOutput.WriteLine("    {0} unique history object(s) checked.", checkedObjects.Count);
    }
}

// One file that can be restored: where it goes (Relative), where its content is (StoredPath), and
// the size, times and checksum it had when backed up.
internal class RestoreEntry
{
    public string Relative, StoredPath, Hash;
    public long Size, ModifiedTicks, CreatedTicks;
}

// The files and folders of one point in time, from a manifest or from the current backup files.
internal class Snapshot
{
    public string Name;
    public DateTime TakenUtc;
    public string CustomName; // optional name the user gave this restore point
    public List<RestoreEntry> Files = new List<RestoreEntry>();
    public List<string> Folders = new List<string>();
    public HashSet<string> OldVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

// The content objects of one backup's store. A restore point can name thousands of them, and every saved
// point repeats the same names, so each object is looked at once per instance: the store's folder is
// checked for links once and each object file once. Use one instance per operation, never across them.
internal sealed class ObjectChecks
{
    readonly string root;
    readonly Dictionary<string, bool> present = new Dictionary<string, bool>(StringComparer.Ordinal);

    internal ObjectChecks(string backup)
    {
        root = Path.GetFullPath(Path.Combine(AppIdentity.MetadataRoot(backup), "objects"));
        PathSafety.RejectRootLinks(root);
    }

    // Where the content with this checksum is stored; the checksum alone names the file.
    internal string PathOf(string hash)
    {
        if (!Snapshots.ValidHash(hash)) throw new IOException("Invalid snapshot checksum.");
        return Path.Combine(root, hash);
    }

    // Whether the object exists. A linked object is refused.
    internal bool Present(string hash)
    {
        bool found;
        if (present.TryGetValue(hash, out found)) return found;
        var info = new FileInfo(PathOf(hash));
        if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are not supported: " + info.FullName);
        present[hash] = info.Exists;
        return info.Exists;
    }
}
