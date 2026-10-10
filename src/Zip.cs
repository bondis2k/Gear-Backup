// Saves a finished full backup as a zip next to its folder (--zip, or "Also save as zip" in the GUI).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

partial class M8Backup
{
    // "<history>\2026-10-10.zip" for the backup folder "<history>\2026-10-10" (_2, _3, ... if taken).
    // Kept beside the folder, not inside it, so restores and checks never treat it as card content.
    static string ZipPathFor(string backup)
    {
        string folder = backup.TrimEnd('\\');
        string parent = Path.GetDirectoryName(folder), name = Path.GetFileName(folder);
        string path = Path.Combine(parent, name + ".zip");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(parent, name + "_" + n + ".zip");
        return path;
    }

    // Zips the card's files and folders (empty ones included) plus checksums.sha256 and the statistics,
    // leaving out the .m8backup history store, which only this program uses and would double the size.
    // Written under a temporary name and renamed when complete, so a zip that exists is always whole.
    static string ZipBackup(string backup)
    {
        var entries = new List<FileSystemInfo>(BackupEntries(backup));
        foreach (string path in Directory.GetFiles(backup))
            if (IsStatisticsFile(path) || Path.GetFileName(path).Equals(ChecksumFileName, StringComparison.OrdinalIgnoreCase))
                entries.Add(new FileInfo(path));
        long total = 0;
        int fileCount = 0;
        foreach (FileSystemInfo entry in entries)
        {
            var file = entry as FileInfo;
            if (file != null) { total += file.Length; fileCount++; }
        }

        string zipPath = ZipPathFor(backup), temp = zipPath + ".tmp";
        TryDelete(temp);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var buffer = new byte[ChunkSize];
                long done = 0;
                int count = 0;
                foreach (FileSystemInfo entry in entries)
                {
                    string name = entry.FullName.Substring(backup.Length).TrimStart('\\').Replace('\\', '/');
                    if (entry is DirectoryInfo)
                    {
                        zip.CreateEntry(name + "/");
                        continue;
                    }
                    var file = (FileInfo)entry;
                    // Fastest: most of a card is WAV samples, which barely compress at any level.
                    ZipArchiveEntry item = zip.CreateEntry(name, CompressionLevel.Fastest);
                    item.LastWriteTime = ZipTime(file.LastWriteTime);
                    using (Stream from = IsStatisticsFile(file.FullName)
                        ? new MemoryStream(Encoding.UTF8.GetBytes(RedactPaths(File.ReadAllText(file.FullName), backup)))
                        : (Stream)new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                    using (Stream to = item.Open())
                    {
                        int read;
                        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            OperationContext.Current.CheckCancellation();
                            to.Write(buffer, 0, read);
                        }
                    }
                    done += file.Length;
                    count++;
                    OperationContext.Current.Progress(Stage.Zipping, name, done, total, count, fileCount);
                }
            }
            File.Move(temp, zipPath);
            return zipPath;
        }
        catch (Exception)
        {
            TryDelete(temp);
            throw;
        }
    }

    // A zip is made to be shared, so its copy of the statistics hides where things are on this PC:
    // the backup folder becomes "<this backup>" and the user profile (C:\Users\<name>) "%USERPROFILE%".
    // The statistics file in the backup folder itself keeps the full paths.
    static string RedactPaths(string text, string backup)
    {
        text = Regex.Replace(text, Regex.Escape(backup.TrimEnd('\\')), "<this backup>", RegexOptions.IgnoreCase);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (profile.Length > 3)
            text = Regex.Replace(text, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
        return text;
    }

    // Zip files can only store dates from 1980 to 2107; undated M8 files (1601) get 1 January 1980.
    static DateTimeOffset ZipTime(DateTime local)
    {
        return local.Year < 1980 || local.Year > 2107 ? new DateTime(1980, 1, 1) : local;
    }
}
