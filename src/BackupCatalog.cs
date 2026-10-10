using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

partial class GearBackup
{
    internal sealed class CatalogBackup
    {
        public string Profile, History, Path, Status, CheckResult;
        public DateTime? CompletedUtc, CheckedUtc;
        public long FilesBytes, VersionsBytes, ObjectsBytes, MetadataBytes, UniqueBytes;
        public bool Latest;
        public long TotalBytes { get { return FilesBytes + VersionsBytes + ObjectsBytes + MetadataBytes; } }
    }
    internal sealed class StorageItem
    {
        public string Kind, Path, History, Backup, Stamp;
        public long Bytes;
        public bool Folder;
    }
    internal sealed class BackupCatalog
    {
        public readonly List<CatalogBackup> Backups = new List<CatalogBackup>();
        public readonly List<StorageItem> Cleanup = new List<StorageItem>();
        public readonly List<string> Warnings = new List<string>();
        public long ZipBytes, MirrorBytes, UniqueBytes;
    }

    // Strict traversal includes metadata, but never follows a junction, symbolic link or hidden root redirection.
    static List<FileInfo> StorageFiles(string root, List<DirectoryInfo> folders = null)
    {
        RejectRootLinks(root);
        var files = new List<FileInfo>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            OperationContext.Current.CheckCancellation();
            string directory = pending.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked storage item: " + entry.FullName);
                if (entry is DirectoryInfo) { pending.Push(entry.FullName); if (folders != null) folders.Add((DirectoryInfo)entry); } else files.Add((FileInfo)entry);
            }
        }
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName));
        return files;
    }
    static string StorageStamp(string path, bool folder, out long bytes)
    {
        RejectRootLinks(path);
        bytes = 0;
        var stamp = new StringBuilder();
        var directories = new List<DirectoryInfo>();
        var files = folder ? StorageFiles(path, directories) : new List<FileInfo> { new FileInfo(path) };
        foreach (FileInfo file in files)
        {
            if (!file.Exists) throw new IOException("Storage item disappeared: " + path);
            bytes += file.Length;
            stamp.Append(file.FullName).Append('\t').Append(file.Length).Append('\t').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
        }
        directories.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName));
        foreach (DirectoryInfo directory in directories) stamp.Append(directory.FullName).Append('\t').Append(directory.LastWriteTimeUtc.Ticks).Append('\n');
        if (folder) stamp.Append(new DirectoryInfo(path).LastWriteTimeUtc.Ticks);
        // This stamp guards changes since preview; it is not an integrity checksum.
        using (var sha = System.Security.Cryptography.SHA256.Create()) return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(stamp.ToString())));
    }
    static StorageItem MakeStorageItem(string kind, string path, string history, string backup, bool folder)
    {
        long bytes;
        string stamp = StorageStamp(path, folder, out bytes);
        return new StorageItem { Kind = kind, Path = path, History = history, Backup = backup, Folder = folder, Stamp = stamp, Bytes = bytes };
    }
    static BackupCatalog ScanBackupCatalog(Config config, string mirrorRoot = null)
    {
        var catalog = new BackupCatalog();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenHistories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string location in ConfiguredBackupLocations(config))
        {
            try
            {
                string root = HistoriesRoot(new Config { BackupFolder = location });
                if (!Directory.Exists(root)) continue;
                RejectRootLinks(root);
                var histories = new List<string> { root };
                string profiles = Path.Combine(root, "profiles");
                if (Directory.Exists(profiles))
                {
                    RejectRootLinks(profiles);
                    histories.AddRange(Directory.GetDirectories(profiles));
                }
                foreach (string history in histories)
                {
                    if (!seenHistories.Add(Rooted(history))) continue;
                    try
                    {
                        RejectRootLinks(history);
                        List<string> backups = ListBackups(history);
                        for (int i = 0; i < backups.Count; i++)
                        {
                            string backup = backups[i];
                            try
                            {
                                List<FileInfo> storedFiles = StorageFiles(backup);
                                HashSet<string> oldVersionNames = KnownOldVersions(backup);
                                var row = new CatalogBackup { Profile = history == root ? "Legacy backups" : Path.GetFileName(history),
                                    History = history, Path = backup, Status = BackupStatus(backup), Latest = i == backups.Count - 1 };
                                var versions = new List<string>();
                                foreach (FileInfo file in storedFiles)
                                {
                                    string relative = file.FullName.Substring(Rooted(backup).Length);
                                    if (AppIdentity.IsMetadataPath(file.FullName))
                                    {
                                        if (relative.IndexOf("\\objects\\", StringComparison.OrdinalIgnoreCase) >= 0) row.ObjectsBytes += file.Length;
                                        else row.MetadataBytes += file.Length;
                                    }
                                    else if (IsBackupMetaFile(file.FullName)) row.MetadataBytes += file.Length;
                                    else if (oldVersionNames.Contains(relative)) { row.VersionsBytes += file.Length; versions.Add(file.FullName); }
                                    else row.FilesBytes += file.Length;
                                    string id = SharedFileId(file.FullName) ?? file.FullName;
                                    if (identities.Add(id)) { row.UniqueBytes += file.Length; catalog.UniqueBytes += file.Length; }
                                }
                                string successPath = Path.Combine(AppIdentity.MetadataRoot(backup), "last-success.txt");
                                if (File.Exists(successPath))
                                {
                                    RejectRootLinks(successPath); DateTime date;
                                    if (!DateTime.TryParseExact(File.ReadAllText(successPath).Trim(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date)) throw new IOException("Invalid last successful backup record.");
                                    row.CompletedUtc = date.ToUniversalTime();
                                }
                                else if (row.Status == Status.Complete)
                                {
                                    string statusPath = Path.Combine(AppIdentity.MetadataRoot(backup), "status.txt");
                                    row.CompletedUtc = File.GetLastWriteTimeUtc(statusPath);
                                }
                                ReadCheckRecord(row);
                                catalog.Backups.Add(row);
                                if (!row.Latest) catalog.Cleanup.Add(MakeStorageItem("Older backup", backup, history, backup, true));
                                foreach (string version in versions) catalog.Cleanup.Add(MakeStorageItem("Old version", version, history, backup, false));
                            }
                            catch (Exception ex) { if (ex is OperationCanceledException) throw; catalog.Warnings.Add(backup + ": " + ex.Message); }
                        }
                        foreach (string zip in Directory.GetFiles(history, "*.zip"))
                        {
                            string name = Path.GetFileNameWithoutExtension(zip);
                            DateTime date;
                            if (name.Length < 10 || !DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                            var item = MakeStorageItem("ZIP", zip, history, null, false);
                            catalog.ZipBytes += item.Bytes;
                            if (identities.Add(SharedFileId(zip) ?? zip)) catalog.UniqueBytes += item.Bytes;
                            catalog.Cleanup.Add(item);
                        }
                    }
                    catch (Exception ex) { if (ex is OperationCanceledException) throw; catalog.Warnings.Add(history + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { if (ex is OperationCanceledException) throw; catalog.Warnings.Add(location + ": " + ex.Message); }
        }
        string mirrors = mirrorRoot ?? MirrorRoot;
        if (Directory.Exists(mirrors)) try
        {
            RejectRootLinks(mirrors);
            foreach (string mirror in Directory.GetDirectories(mirrors))
            {
                try
                {
                    if (!Array.Exists(Devices.All, d => Path.GetFileName(mirror).StartsWith(d.Id + "-", StringComparison.OrdinalIgnoreCase))) continue;
                    var item = MakeStorageItem("MTP mirror", mirror, mirrors, null, true);
                    catalog.MirrorBytes += item.Bytes;
                    foreach (FileInfo file in StorageFiles(mirror)) if (identities.Add(SharedFileId(file.FullName) ?? file.FullName)) catalog.UniqueBytes += file.Length;
                    catalog.Cleanup.Add(item);
                }
                catch (Exception ex) { if (ex is OperationCanceledException) throw; catalog.Warnings.Add(mirror + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { if (ex is OperationCanceledException) throw; catalog.Warnings.Add(mirrors + ": " + ex.Message); }
        return catalog;
    }
    static void ReadCheckRecord(CatalogBackup row)
    {
        string path = Path.Combine(AppIdentity.MetadataRoot(row.Path), "last-check.txt");
        if (!File.Exists(path)) { row.CheckResult = "Never checked"; return; }
        RejectRootLinks(path);
        string[] lines = File.ReadAllLines(path);
        DateTime checkedUtc;
        if (lines.Length != 2 || !DateTime.TryParseExact(lines[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out checkedUtc))
            throw new IOException("Invalid integrity check record.");
        row.CheckedUtc = checkedUtc.ToUniversalTime(); row.CheckResult = lines[1];
        string status = Path.Combine(AppIdentity.MetadataRoot(row.Path), "status.txt");
        if (File.Exists(status) && File.GetLastWriteTimeUtc(status) > row.CheckedUtc.Value) row.CheckResult += " (previous state)";
    }
    static void SaveCheckRecord(string backup, int ok, int damaged, int missing, int uncheckedFiles)
    {
        RejectRootLinks(backup);
        string result = damaged > 0 || missing > 0 ? "Problems found" : uncheckedFiles > 0 ? "Incomplete check" : "OK";
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "last-check.txt"); RejectRootLinks(path);
        AtomicText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n" + result + "\n");
    }
    // The caller supplies only items from a completed scan. Revalidate all candidates under locks before mutation.
    static int CleanupStorage(BackupCatalog catalog, List<StorageItem> selected, Action<StorageItem> recycle = null)
    {
        if (PreviewOnly) throw new IOException("Cleanup cannot run in preview-only mode.");
        var items = new List<StorageItem>();
        foreach (StorageItem item in selected)
        {
            if (!catalog.Cleanup.Contains(item)) throw new IOException("Unrecognised cleanup item.");
            if (!items.Contains(item)) items.Add(item);
        }
        items.RemoveAll(item => items.Exists(parent => parent != item && parent.Folder && Within(item.Path, parent.Path)));
        var roots = new List<string>();
        foreach (StorageItem item in items) if (!roots.Exists(r => Rooted(r) == Rooted(item.History))) roots.Add(item.History);
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        var locks = new List<IDisposable>();
        try
        {
            foreach (string root in roots) locks.Add(LockHistory(root));
            foreach (StorageItem item in items)
            {
                if (!Within(item.Path, item.History) || Rooted(item.Path) == Rooted(item.History)) throw new IOException("Cleanup path escaped its storage root.");
                if (item.Kind == "Older backup" && Rooted(item.Path) == Rooted(LatestBackup(item.History))) throw new IOException("The latest backup is protected.");
                long bytes;
                if (StorageStamp(item.Path, item.Folder, out bytes) != item.Stamp) throw new IOException("Storage changed since the preview. Refresh before cleanup: " + item.Path);
            }
            int removed = 0;
            foreach (StorageItem item in items)
            {
                OperationContext.Current.CheckCancellation();
                long currentBytes;
                if (StorageStamp(item.Path, item.Folder, out currentBytes) != item.Stamp)
                    throw new IOException("Storage changed before recycling. Refresh before cleanup: " + item.Path);
                if (recycle != null) recycle(item);
                else if (item.Folder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                removed++;
                if (item.Kind == "Old version" && Directory.Exists(item.Backup))
                {
                    Dictionary<string, string> sums = LoadChecksums(item.Backup);
                    if (sums != null) { sums.Remove(item.Path.Substring(Rooted(item.Backup).Length)); WriteChecksums(item.Backup, sums); }
                }
            }
            return removed;
        }
        finally { for (int i = locks.Count - 1; i >= 0; i--) locks[i].Dispose(); }
    }
}
