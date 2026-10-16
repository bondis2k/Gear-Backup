// Saves a finished full backup as a zip next to its folder (--zip, or "Also save as zip" in the GUI).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using static GearBackup.FileCopy;
using static GearBackup.Backups;
using static GearBackup.PathSafety;

namespace GearBackup
{
    static class BackupZip
    {
        // "<history>\2026-10-10.zip" for the backup folder "<history>\2026-10-10" (_2, _3, ... if taken).
        // Kept beside the folder, not inside it, so restores and checks never treat it as card content.
        internal static string ZipPathFor(string backup)
        {
            string folder = backup.TrimEnd('\\');
            string parent = Path.GetDirectoryName(folder), name = Path.GetFileName(folder);
            string path = Path.Combine(parent, name + ".zip");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(parent, name + "_" + n + ".zip");
            return path;
        }

        // Zips the latest restore point as ordinary files and folders (empty folders included) plus checksums.sha256.
        // Written under a temporary name and renamed when complete, so a zip that exists is always whole.
        internal static string ZipBackup(string backup)
        {
            var snapshot = ContentStore.Current(backup);
            string path = ZipPathFor(backup), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            RejectRootLinks(path);
            bool created = false;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                    {
                        var buffer = new byte[65536]; var sums = new StringBuilder(); int count = 0;
                        foreach (string folder in snapshot.Folders) zip.CreateEntry(folder.Replace('\\', '/') + "/");
                        foreach (var item in snapshot.Files)
                        {
                            OperationContext.Current.CheckCancellation();
                            PathSafety.RejectRootLinks(item.StoredPath);
                            if (!File.Exists(item.StoredPath) || new FileInfo(item.StoredPath).Length != item.Size) throw new IOException("Stored content is missing or damaged: " + item.Relative);
                            string name = item.Relative.Replace('\\', '/');
                            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                            entry.LastWriteTime = ZipTime(new DateTime(item.ModifiedTicks, DateTimeKind.Utc).ToLocalTime());
                            using (var from = new FileStream(item.StoredPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var to = entry.Open())
                            {
                                int read; while ((read = from.Read(buffer, 0, buffer.Length)) != 0)
                                { OperationContext.Current.CheckCancellation(); to.Write(buffer, 0, read); }
                            }
                            sums.Append(item.Hash).Append("  ").Append(name).Append('\n');
                            OperationContext.Current.Progress(Stage.Zipping, item.Relative, 0, 0, ++count, snapshot.Files.Count);
                        }
                        using (var writer = new StreamWriter(zip.CreateEntry(ArchiveMetadata.ChecksumFileName).Open(), new UTF8Encoding(false))) writer.Write(sums.ToString());
                    }
                }
                OperationContext.Current.CheckCancellation();
                RejectRootLinks(path); File.Move(temp, path); return path;
            }
            finally { if (created) TryDelete(temp); }
        }

        // Zip files can only store dates from 1980 to 2107; undated M8 files (1601) get 1 January 1980.
        internal static DateTimeOffset ZipTime(DateTime local)
        {
            return local.Year < 1980 || local.Year > 2107 ? new DateTime(1980, 1, 1) : local;
        }
    }

}
