using System;
using System.IO;
using System.Collections.Generic;
using static Checksums;

static class BackupTraversal
{
    // Traverse user content without descending into the history object store.
    internal static IEnumerable<FileSystemInfo> BackupEntries(string root)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            OperationContext.Current.Progress(Stage.ScanningBackup, entry.FullName, 0, 0);
            if (IsBackupMetaFile(entry.FullName)) continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked backup paths are not supported: " + entry.FullName);
            yield return entry;
            if (entry is DirectoryInfo)
                foreach (FileSystemInfo child in BackupEntries(entry.FullName)) yield return child;
        }
    }
    internal static List<string> BackupFiles(string root)
    {
        var files = new List<string>();
        foreach (FileSystemInfo entry in BackupEntries(root)) if (entry is FileInfo) files.Add(entry.FullName);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
