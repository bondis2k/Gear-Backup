// What the Dashboard and Storage windows show: every backup in every configured backup location, with
// its status, last check and how its space is used, plus ZIP archives and MTP mirrors. Storage cleanup
// moves selected items to the Recycle Bin, after checking they haven't changed since the scan.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

partial class GearBackup
{
    // One backup folder as the Dashboard and Storage windows see it.
    internal sealed class CatalogBackup
    {
        public string Profile, History, Path, Status, CheckResult;
        public DateTime? CompletedUtc, CheckedUtc;
        public long FilesBytes, VersionsBytes, ObjectsBytes, MetadataBytes, UniqueBytes;
        public bool Latest;
        public long TotalBytes { get { return FilesBytes + VersionsBytes + ObjectsBytes + MetadataBytes; } }
    }

    // Something Storage offers to recycle. Stamp records its state at scan time (see StorageStamp).
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
        // Locations, histories or backups that couldn't be read; the scan carries on without them.
        public readonly List<string> Warnings = new List<string>();
        public long ZipBytes, MirrorBytes, UniqueBytes;
        // Each file's identity once counted, so hard-linked content counts once in UniqueBytes.
        public readonly HashSet<string> Counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // Every file below root, including metadata, sorted by path; folders are added to `folders` when given.
    // Never follows a junction or symbolic link: any link is an error.
    static List<FileInfo> StorageFiles(string root, List<DirectoryInfo> folders = null)
    {
        RejectRootLinks(root);
        var files = new List<FileInfo>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            OperationContext.Current.CheckCancellation();
            foreach (FileSystemInfo entry in new DirectoryInfo(pending.Pop()).GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked storage item: " + entry.FullName);
                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    if (folders != null) folders.Add((DirectoryInfo)entry);
                }
                else files.Add((FileInfo)entry);
            }
        }
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName));
        return files;
    }

    // A fingerprint of an item's files and folders (names, sizes, times), to notice changes between the
    // scan and the cleanup. It guards against changes; it is not an integrity checksum.
    static string StorageStamp(string path, bool folder, out long bytes)
    {
        RejectRootLinks(path);
        var directories = new List<DirectoryInfo>();
        var files = folder ? StorageFiles(path, directories) : new List<FileInfo> { new FileInfo(path) };
        return StorageStamp(path, folder, files, directories, out bytes);
    }

    // The same stamp from a listing the caller already has, so a scan reads each tree once.
    static string StorageStamp(string path, bool folder, List<FileInfo> files, List<DirectoryInfo> directories, out long bytes)
    {
        bytes = 0;
        var stamp = new StringBuilder();
        foreach (FileInfo file in files)
        {
            if (!file.Exists) throw new IOException("Storage item disappeared: " + path);
            bytes += file.Length;
            stamp.Append(file.FullName).Append('\t').Append(file.Length).Append('\t').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
        }
        var sorted = new List<DirectoryInfo>(directories);
        sorted.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName));
        foreach (DirectoryInfo directory in sorted)
            stamp.Append(directory.FullName).Append('\t').Append(directory.LastWriteTimeUtc.Ticks).Append('\n');
        if (folder) stamp.Append(new DirectoryInfo(path).LastWriteTimeUtc.Ticks);
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(stamp.ToString())));
    }

    static StorageItem MakeStorageItem(string kind, string path, string history, string backup, bool folder)
    {
        long bytes;
        string stamp = StorageStamp(path, folder, out bytes);
        return new StorageItem { Kind = kind, Path = path, History = history, Backup = backup, Folder = folder, Stamp = stamp, Bytes = bytes };
    }

    static StorageItem MakeStorageItem(string kind, string path, string history, string backup, List<FileInfo> files, List<DirectoryInfo> directories)
    {
        long bytes;
        string stamp = StorageStamp(path, true, files, directories, out bytes);
        return new StorageItem { Kind = kind, Path = path, History = history, Backup = backup, Folder = true, Stamp = stamp, Bytes = bytes };
    }

    // Runs one part of a scan; if it fails, notes why and carries on (cancellation still stops the scan).
    static void ScanPart(BackupCatalog catalog, string path, Action scan)
    {
        try { scan(); }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) throw;
            catalog.Warnings.Add(path + ": " + ex.Message);
        }
    }

    // Counts a file's size towards the unique total once per identity (see FileIdentities).
    static void CountUnique(BackupCatalog catalog, string identity, long length, CatalogBackup row = null)
    {
        if (!catalog.Counted.Add(identity)) return;
        catalog.UniqueBytes += length;
        if (row != null) row.UniqueBytes += length;
    }

    // What tells files apart for the unique total, by path: hard links to one file share an identity. On
    // NTFS that's the file's ID, read a whole folder at a time; opening every file instead took seconds on
    // a large history. File systems without hard links (FAT, exFAT) just use the path.
    static Dictionary<string, string> FileIdentities(string root, List<FileInfo> files)
    {
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!SupportsHardLinks(root))
        {
            foreach (FileInfo file in files) identities[file.FullName] = file.FullName;
            return identities;
        }
        uint serial, maxComponent, flags;
        string volume = GetVolumeInformationW(Path.GetPathRoot(Path.GetFullPath(root)), null, 0, out serial, out maxComponent, out flags, null, 0)
            ? serial.ToString(CultureInfo.InvariantCulture) : null;
        var folders = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in files)
        {
            Dictionary<string, long> ids;
            if (!folders.TryGetValue(file.DirectoryName, out ids)) folders[file.DirectoryName] = ids = volume == null ? null : FolderFileIds(file.DirectoryName);
            long id;
            identities[file.FullName] = ids != null && ids.TryGetValue(file.Name, out id) && id != 0
                ? volume + ":" + (ulong)id
                : SharedFileId(file.FullName) ?? file.FullName;
        }
        return identities;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, IntPtr buffer, uint size);

    // The NTFS file ID of every entry in a folder, by name, from FILE_ID_BOTH_DIR_INFO records (FileId at
    // offset 96, FileNameLength at 60, FileName at 104). Null if Windows can't list it this way.
    static Dictionary<string, long> FolderFileIds(string folder)
    {
        const uint ListDirectory = 1, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        const int RestartInfo = 11, NextInfo = 10, NoMoreFiles = 18, Size = 64 * 1024;
        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (SafeFileHandle handle = CreateFileW(folder, ListDirectory, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero))
        {
            if (handle.IsInvalid) return null;
            IntPtr buffer = Marshal.AllocHGlobal(Size);
            try
            {
                int infoClass = RestartInfo;
                while (GetFileInformationByHandleEx(handle, infoClass, buffer, Size))
                {
                    infoClass = NextInfo;
                    for (int offset = 0; ; )
                    {
                        IntPtr entry = IntPtr.Add(buffer, offset);
                        int next = Marshal.ReadInt32(entry, 0);
                        int nameBytes = Marshal.ReadInt32(entry, 60);
                        ids[Marshal.PtrToStringUni(IntPtr.Add(entry, 104), nameBytes / 2)] = Marshal.ReadInt64(entry, 96);
                        if (next == 0) break;
                        offset += next;
                    }
                }
                if (Marshal.GetLastWin32Error() != NoMoreFiles) return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return ids;
    }

    // Scans every configured backup location (the global one and those named in device/profile
    // preferences) and the MTP mirrors. mirrorRoot is for tests.
    static BackupCatalog ScanBackupCatalog(Config config, string mirrorRoot = null)
    {
        var catalog = new BackupCatalog();
        var seenHistories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string location in ConfiguredBackupLocations(config))
            ScanPart(catalog, location, () => ScanLocation(catalog, location, seenHistories));
        string mirrors = mirrorRoot ?? MirrorRoot;
        if (Directory.Exists(mirrors)) ScanPart(catalog, mirrors, () => ScanMirrors(catalog, mirrors));
        return catalog;
    }

    // A location's legacy history (backups directly under Gear Backups) and its profiles.
    static void ScanLocation(BackupCatalog catalog, string location, HashSet<string> seenHistories)
    {
        string root = HistoriesRoot(new Config { BackupFolder = location });
        if (!Directory.Exists(root)) return;
        RejectRootLinks(root);
        var histories = new List<string> { root };
        string profiles = Path.Combine(root, "profiles");
        if (Directory.Exists(profiles))
        {
            RejectRootLinks(profiles);
            histories.AddRange(Directory.GetDirectories(profiles));
        }
        foreach (string history in histories)
            if (seenHistories.Add(Rooted(history)))
                ScanPart(catalog, history, () => ScanHistory(catalog, history, history == root ? "Legacy backups" : Path.GetFileName(history)));
    }

    // A history's backup folders and its ZIP archives. Every backup but the latest can be cleaned up.
    static void ScanHistory(BackupCatalog catalog, string history, string profile)
    {
        RejectRootLinks(history);
        List<string> backups = ListBackups(history);
        for (int i = 0; i < backups.Count; i++)
        {
            string backup = backups[i];
            bool latest = i == backups.Count - 1;
            ScanPart(catalog, backup, () => ScanBackup(catalog, history, profile, backup, latest));
        }
        foreach (string zip in Directory.GetFiles(history, "*.zip"))
        {
            string name = Path.GetFileNameWithoutExtension(zip);
            DateTime date;
            if (name.Length < 10 || !DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                continue;
            StorageItem item = MakeStorageItem(StorageKind.Zip, zip, history, null, false);
            catalog.ZipBytes += item.Bytes;
            CountUnique(catalog, SharedFileId(zip) ?? zip, item.Bytes);
            catalog.Cleanup.Add(item);
        }
    }

    // One backup folder: its space by kind (current files, known old versions, history objects, other
    // metadata), when it last succeeded, and its last integrity check.
    static void ScanBackup(BackupCatalog catalog, string history, string profile, string backup, bool latest)
    {
        var folders = new List<DirectoryInfo>();
        List<FileInfo> files = StorageFiles(backup, folders);
        HashSet<string> oldVersionNames = KnownOldVersions(backup);
        var row = new CatalogBackup { Profile = profile, History = history, Path = backup, Status = BackupStatus(backup), Latest = latest };
        Dictionary<string, string> identities = FileIdentities(backup, files);
        var versions = new List<string>();
        foreach (FileInfo file in files)
        {
            string relative = file.FullName.Substring(Rooted(backup).Length);
            if (AppIdentity.IsMetadataPath(file.FullName))
            {
                if (relative.IndexOf("\\objects\\", StringComparison.OrdinalIgnoreCase) >= 0) row.ObjectsBytes += file.Length;
                else row.MetadataBytes += file.Length;
            }
            else if (IsBackupMetaFile(file.FullName)) row.MetadataBytes += file.Length;
            else if (oldVersionNames.Contains(relative))
            {
                row.VersionsBytes += file.Length;
                versions.Add(file.FullName);
            }
            else row.FilesBytes += file.Length;
            CountUnique(catalog, identities[file.FullName], file.Length, row);
        }
        row.CompletedUtc = LastSuccess(backup, row.Status);
        ReadCheckRecord(row);
        catalog.Backups.Add(row);
        if (!latest) catalog.Cleanup.Add(MakeStorageItem(StorageKind.OlderBackup, backup, history, backup, files, folders));
        foreach (string version in versions) catalog.Cleanup.Add(MakeStorageItem(StorageKind.OldVersion, version, history, backup, false));
    }

    // When the backup last completed successfully: its own record, or for a complete backup made before
    // those records existed, its status file's time. Null otherwise.
    static DateTime? LastSuccess(string backup, string status)
    {
        string successPath = Path.Combine(AppIdentity.MetadataRoot(backup), "last-success.txt");
        if (File.Exists(successPath))
        {
            RejectRootLinks(successPath);
            CheckRecordSize(successPath, SmallRecordLimit, "Last successful backup record");
            DateTime date;
            if (!DateTime.TryParseExact(File.ReadAllText(successPath).Trim(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date))
                throw new IOException("Invalid last successful backup record.");
            return date.ToUniversalTime();
        }
        if (status != Status.Complete) return null;
        return File.GetLastWriteTimeUtc(Path.Combine(AppIdentity.MetadataRoot(backup), "status.txt"));
    }

    // The MTP mirrors of known devices; each can be recycled (the next backup downloads it again).
    static void ScanMirrors(BackupCatalog catalog, string mirrors)
    {
        RejectRootLinks(mirrors);
        foreach (string mirror in Directory.GetDirectories(mirrors))
        {
            if (!Array.Exists(Devices.All, d => Path.GetFileName(mirror).StartsWith(d.Id + "-", StringComparison.OrdinalIgnoreCase))) continue;
            ScanPart(catalog, mirror, delegate {
                RejectRootLinks(mirror);
                var folders = new List<DirectoryInfo>();
                List<FileInfo> files = StorageFiles(mirror, folders);
                StorageItem item = MakeStorageItem(StorageKind.Mirror, mirror, mirrors, null, files, folders);
                catalog.MirrorBytes += item.Bytes;
                Dictionary<string, string> identities = FileIdentities(mirror, files);
                foreach (FileInfo file in files) CountUnique(catalog, identities[file.FullName], file.Length);
                catalog.Cleanup.Add(item);
            });
        }
    }

    // The latest integrity check of a backup, from last-check.txt ("Never checked" without one). A check
    // older than the backup's status is marked as describing a previous state.
    static void ReadCheckRecord(CatalogBackup row)
    {
        string path = Path.Combine(AppIdentity.MetadataRoot(row.Path), "last-check.txt");
        if (!File.Exists(path)) { row.CheckResult = "Never checked"; return; }
        RejectRootLinks(path);
        CheckRecordSize(path, SmallRecordLimit, "Integrity check record");
        string[] lines = File.ReadAllLines(path);
        DateTime checkedUtc;
        if (lines.Length != 2 || !DateTime.TryParseExact(lines[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out checkedUtc))
            throw new IOException("Invalid integrity check record.");
        row.CheckedUtc = checkedUtc.ToUniversalTime();
        row.CheckResult = lines[1];
        string status = Path.Combine(AppIdentity.MetadataRoot(row.Path), "status.txt");
        if (File.Exists(status) && File.GetLastWriteTimeUtc(status) > row.CheckedUtc.Value) row.CheckResult += " (previous state)";
    }

    static void SaveCheckRecord(string backup, int ok, int damaged, int missing, int uncheckedFiles)
    {
        RejectRootLinks(backup);
        string result = damaged > 0 || missing > 0 ? "Problems found" : uncheckedFiles > 0 ? "Incomplete check" : "OK";
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "last-check.txt");
        RejectRootLinks(path);
        AtomicText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n" + result + "\n");
    }

    // Moves the selected items to the Recycle Bin (or passes them to `recycle`, for tests). Only items from
    // this catalog are accepted; under the locks of their histories, each is checked again (still inside its
    // history, not the latest backup, unchanged since the scan) before anything is recycled.
    static int CleanupStorage(BackupCatalog catalog, List<StorageItem> selected, Action<StorageItem> recycle = null)
    {
        if (PreviewOnly) throw new IOException("Cleanup cannot run in preview-only mode.");
        var items = new List<StorageItem>();
        foreach (StorageItem item in selected)
        {
            if (!catalog.Cleanup.Contains(item)) throw new IOException("Unrecognised cleanup item.");
            if (!items.Contains(item)) items.Add(item);
        }
        // An item inside a selected folder goes with the folder.
        items.RemoveAll(item => items.Exists(parent => parent != item && parent.Folder && Within(item.Path, parent.Path)));
        var roots = new List<string>();
        foreach (StorageItem item in items)
            if (!roots.Exists(r => Rooted(r) == Rooted(item.History))) roots.Add(item.History);
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        var locks = new List<IDisposable>();
        try
        {
            foreach (string root in roots) locks.Add(LockHistory(root));
            foreach (StorageItem item in items)
            {
                if (!Within(item.Path, item.History) || Rooted(item.Path) == Rooted(item.History))
                    throw new IOException("Cleanup path escaped its storage root.");
                if (item.Kind == StorageKind.OlderBackup && Rooted(item.Path) == Rooted(LatestBackup(item.History)))
                    throw new IOException("The latest backup is protected.");
                long bytes;
                if (StorageStamp(item.Path, item.Folder, out bytes) != item.Stamp)
                    throw new IOException("Storage changed since the preview. Refresh before cleanup: " + item.Path);
            }
            int removed = 0;
            // Recycled old versions by backup, whose checksum lists are rewritten once at the end.
            var recycledVersions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool finished = false;
            try
            {
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
                    if (item.Kind != StorageKind.OldVersion) continue;
                    List<string> names;
                    if (!recycledVersions.TryGetValue(item.Backup, out names)) recycledVersions[item.Backup] = names = new List<string>();
                    names.Add(item.Path.Substring(Rooted(item.Backup).Length));
                }
                finished = true;
            }
            finally
            {
                // Also after a failure or Cancel part-way, so the lists never name a recycled version.
                // An error here only replaces the outcome when the cleanup itself succeeded.
                try { RemoveChecksumEntries(recycledVersions); }
                catch (Exception) { if (finished) throw; }
            }
            return removed;
        }
        finally
        {
            for (int i = locks.Count - 1; i >= 0; i--) locks[i].Dispose();
        }
    }

    // Takes recycled old versions out of their backups' checksum lists, one rewrite per backup. Runs
    // without cancellation: a Cancel pressed during cleanup must not leave a list naming recycled files.
    static void RemoveChecksumEntries(Dictionary<string, List<string>> recycled)
    {
        if (recycled.Count == 0) return;
        OperationContext current = OperationContext.Current;
        using (new OperationContext(current.Interaction, System.Threading.CancellationToken.None) { Device = current.Device }.Activate())
            foreach (var backup in recycled)
            {
                if (!Directory.Exists(backup.Key)) continue;
                Dictionary<string, string> sums = LoadChecksums(backup.Key);
                if (sums == null) continue;
                foreach (string name in backup.Value) sums.Remove(name);
                WriteChecksums(backup.Key, sums);
            }
    }
}
