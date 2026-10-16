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
using static FileCopy;
using static PathSafety;
using static Backups;
using static MtpMirror;
using static DeviceSelection;
using static ProfileSettings;
using static Session;
using static AtomicFile;
using static BackupStatuses;
using static Histories;
using static PlanReview;
using static ProfileNaming;
using static StorageCatalog;

static class StorageCatalog
{
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

    // A fingerprint of an item's files (names, sizes, times) and folders (names), to notice changes between
    // the scan and the cleanup. It guards against changes; it is not an integrity checksum. Folder times
    // are left out: Windows updates a folder's time in its parent's listing a few milliseconds late, so a
    // scan right after a backup could disagree with a later check although nothing changed. Any change to
    // a file shows in the file's own entry, and an added or removed folder in the folder names.
    internal static string StorageStamp(string path, bool folder, out long bytes)
    {
        RejectRootLinks(path);
        var directories = new List<DirectoryInfo>();
        var files = folder ? StorageFiles(path, directories) : new List<FileInfo> { new FileInfo(path) };
        return StorageStamp(path, folder, files, directories, out bytes);
    }

    // The same stamp from a listing the caller already has, so a scan reads each tree once.
    internal static string StorageStamp(string path, bool folder, List<FileInfo> files, List<DirectoryInfo> directories, out long bytes)
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
        foreach (DirectoryInfo directory in sorted) stamp.Append(directory.FullName).Append('\\').Append('\n');
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(stamp.ToString())));
    }

    static StorageItem MakeStorageItem(StorageKind kind, string path, string history, string backup, bool folder)
    {
        long bytes;
        string stamp = StorageStamp(path, folder, out bytes);
        return new StorageItem { Kind = kind, Path = path, History = history, Backup = backup, Folder = folder, Stamp = stamp, Bytes = bytes };
    }

    static StorageItem MakeStorageItem(StorageKind kind, string path, string history, string backup, List<FileInfo> files, List<DirectoryInfo> directories)
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

    // Counts a file's size towards the unique total once per path.
    static void CountUnique(BackupCatalog catalog, string identity, long length, CatalogBackup row = null)
    {
        if (!catalog.Counted.Add(identity)) return;
        catalog.UniqueBytes += length;
        if (row != null) row.UniqueBytes += length;
    }

    // Scans every configured backup location (the global one and those named in device/profile
    // preferences) and the MTP mirrors. mirrorRoot is for tests.
    internal static BackupCatalog ScanDashboardCatalog(Config config)
    {
        return ScanBackupCatalog(config, null, false);
    }

    internal static BackupCatalog ScanBackupCatalog(Config config, string mirrorRoot = null, bool storageDetails = true)
    {
        OperationContext.Current.CheckCancellation();
        var catalog = new BackupCatalog { StorageDetails = storageDetails };
        var seenHistories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string location in ConfiguredBackupLocations(config))
            ScanPart(catalog, location, () => ScanLocation(catalog, location, seenHistories));
        string mirrors = mirrorRoot ?? MirrorRoot;
        if (storageDetails && Directory.Exists(mirrors)) ScanPart(catalog, mirrors, () => ScanMirrors(catalog, mirrors));
        return catalog;
    }

    // Profile histories under a configured backup location.
    static void ScanLocation(BackupCatalog catalog, string location, HashSet<string> seenHistories)
    {
        string root = HistoriesRoot(new Config { BackupFolder = location });
        if (!Directory.Exists(root)) return;
        RejectRootLinks(root);
        var histories = new List<string>();
        string profiles = Path.Combine(root, "profiles");
        if (Directory.Exists(profiles))
        {
            RejectRootLinks(profiles);
            histories.AddRange(Directory.GetDirectories(profiles));
        }
        foreach (string history in histories)
            if (seenHistories.Add(Rooted(history)))
                ScanPart(catalog, history, () => ScanHistory(catalog, history, HistoryNames.DisplayName(history)));
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
        if (!catalog.StorageDetails) return;
        foreach (string zip in Directory.GetFiles(history, "*.zip"))
        {
            string name = Path.GetFileNameWithoutExtension(zip);
            DateTime date;
            if (name.Length < 10 || !DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                continue;
            StorageItem item = MakeStorageItem(StorageKind.Zip, zip, history, null, false);
            catalog.ZipBytes += item.Bytes;
            CountUnique(catalog, zip, item.Bytes);
            catalog.Cleanup.Add(item);
        }
    }

    // One backup folder: its space by kind (stored content objects, other metadata, and the files of a
    // backup made by an older version), when it last succeeded, and its last integrity check.
    static void ScanBackup(BackupCatalog catalog, string history, string profile, string backup, bool latest)
    {
        OperationContext.Current.CheckCancellation();
        if (!catalog.StorageDetails)
        {
            RejectRootLinks(backup);
            var metadata = new CatalogBackup { Profile = profile, History = history, Path = backup,
                Status = BackupStatus(backup), Latest = latest, Device = CatalogDevice(backup) };
            metadata.CompletedUtc = LastSuccess(backup);
            ReadCheckRecord(metadata);
            catalog.Backups.Add(metadata);
            return;
        }
        var folders = new List<DirectoryInfo>();
        List<FileInfo> files = StorageFiles(backup, folders);
        var row = new CatalogBackup { Profile = profile, History = history, Path = backup, Status = BackupStatus(backup), Latest = latest,
            Device = CatalogDevice(backup) };
        foreach (FileInfo file in files)
        {
            string relative = file.FullName.Substring(Rooted(backup).Length);
            if (AppIdentity.IsMetadataPath(file.FullName))
            {
                if (relative.IndexOf("\\objects\\", StringComparison.OrdinalIgnoreCase) >= 0) row.ObjectsBytes += file.Length;
                else row.MetadataBytes += file.Length;
            }
            else row.FilesBytes += file.Length;
            CountUnique(catalog, file.FullName, file.Length, row);
        }
        row.CompletedUtc = LastSuccess(backup);
        ReadCheckRecord(row);
        catalog.Backups.Add(row);
        if (!latest) catalog.Cleanup.Add(MakeStorageItem(StorageKind.OlderBackup, backup, history, backup, files, folders));
    }

    // A damaged device record loses the device filter, while the backup remains visible in All devices.
    internal static DeviceProfile CatalogDevice(string backup)
    {
        try { return BackupDevice(backup); }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
            return null;
        }
    }

    // The recorded last successful run. Missing metadata is not inferred from file timestamps.
    static DateTime? LastSuccess(string backup)
    {
        string successPath = Path.Combine(AppIdentity.MetadataRoot(backup), "last-success.txt");
        if (File.Exists(successPath))
        {
            DateTime date;
            if (!DateTime.TryParseExact(Records.ReadText(successPath, Records.SmallLimit, "Last successful backup record").Trim(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date))
                throw new IOException("Invalid last successful backup record.");
            return date.ToUniversalTime();
        }
        return null;
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
                foreach (FileInfo file in files) CountUnique(catalog, file.FullName, file.Length);
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
        string[] lines = Records.ReadLines(path, Records.SmallLimit, "Integrity check record");
        DateTime checkedUtc;
        if (lines.Length != 2 || !DateTime.TryParseExact(lines[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out checkedUtc))
            throw new IOException("Invalid integrity check record.");
        row.CheckedUtc = checkedUtc.ToUniversalTime();
        row.CheckResult = lines[1];
        string status = Path.Combine(AppIdentity.MetadataRoot(row.Path), "status.txt");
        if (File.Exists(status) && File.GetLastWriteTimeUtc(status) > row.CheckedUtc.Value) row.CheckResult += " (previous state)";
    }

    internal static void SaveCheckRecord(string backup, int ok, int damaged, int missing, int uncheckedFiles)
    {
        RejectRootLinks(backup);
        string result = damaged > 0 || missing > 0 ? "Problems found" : uncheckedFiles > 0 ? "Incomplete check" : "OK";
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "last-check.txt");
        AtomicText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n" + result + "\n");
    }

    // Moves the selected items to the Recycle Bin (or passes them to `recycle`, for tests). Only items from
    // this catalog are accepted; under the locks of their histories, each is checked again (still inside its
    // history, not the latest backup, unchanged since the scan) before anything is recycled.
    internal static int CleanupStorage(BackupCatalog catalog, List<StorageItem> selected, Action<StorageItem> recycle = null)
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
                }
            }
            return removed;
        }
        finally
        {
            for (int i = locks.Count - 1; i >= 0; i--) locks[i].Dispose();
        }
    }

}

// One backup folder as the Dashboard and Storage windows see it.
internal sealed class CatalogBackup
{
    public string Profile, History, Path, CheckResult;
    public Status Status;
    public DateTime? CompletedUtc, CheckedUtc;
    public DeviceProfile Device;    // the device it was made from; null when identity metadata is missing/invalid
    public long FilesBytes, ObjectsBytes, MetadataBytes, UniqueBytes;
    public bool Latest;
    public long TotalBytes { get { return FilesBytes + ObjectsBytes + MetadataBytes; } }
}

// Something Storage offers to recycle. Stamp records its state at scan time (see StorageStamp).
internal sealed class StorageItem
{
    public StorageKind Kind;
    public string Path, History, Backup, Stamp;
    public long Bytes;
    public bool Folder;
}

internal sealed class BackupCatalog
{
    public bool StorageDetails = true;
    public readonly List<CatalogBackup> Backups = new List<CatalogBackup>();
    public readonly List<StorageItem> Cleanup = new List<StorageItem>();
    // Locations, histories or backups that couldn't be read; the scan carries on without them.
    public readonly List<string> Warnings = new List<string>();
    public long ZipBytes, MirrorBytes, UniqueBytes;
    // Each file's identity once counted, so hard-linked content counts once in UniqueBytes.
    public readonly HashSet<string> Counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
