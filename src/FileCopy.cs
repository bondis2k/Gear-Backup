// Comparing files, copying with checksum verification, and free-space checks.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

partial class M8Backup
{
    enum FileState { New, Changed, Same }

    // Same size and modified time (within FAT's 2 s resolution) counts as the same file.
    static FileState Compare(FileInfo file, string otherPath)
    {
        var other = new FileInfo(otherPath);
        if (!other.Exists) return FileState.New;
        if (other.Length != file.Length) return FileState.Changed;
        // M8/FAT files can report a missing date as FILETIME zero (1601-01-01).
        // Setting that date on Windows means "leave unchanged", so copies retain
        // the copy time. Compare bytes instead of treating that time as an edit.
        if (!UsableFileTime(file.LastWriteTimeUtc) || !UsableFileTime(other.LastWriteTimeUtc))
            return FilesEqual(file.FullName, otherPath) ? FileState.Same : FileState.Changed;
        return Math.Abs((other.LastWriteTimeUtc - file.LastWriteTimeUtc).TotalSeconds) <= 2 ? FileState.Same : FileState.Changed;
    }

    static bool UsableFileTime(DateTime time)
    {
        return time >= new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    // Exact comparison for missing timestamps; no SHA-256 calculation/comparison.
    static bool FilesEqual(string first, string second)
    {
        using (var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (a.Length != b.Length) return false;
            byte[] left = new byte[65536], right = new byte[65536];
            int count;
            while ((count = ReadBlock(a, left)) > 0)
            {
                OperationContext.Current.Progress("Comparing", first, a.Position, a.Length);
                if (ReadBlock(b, right) != count) return false;
                for (int i = 0; i < count; i++) if (left[i] != right[i]) return false;
            }
            return b.ReadByte() == -1;
        }
    }

    static int ReadBlock(Stream stream, byte[] buffer)
    {
        int count = 0, read;
        while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0) count += read;
        return count;
    }

    // Copies a file (target must not exist yet) and returns the SHA-256 checksum of the original.
    // With verify, the copy is read back and compared; a mismatch is retried once, then reported as a failure.
    // A failed copy never leaves a partial file behind.
    static string CopyFile(FileInfo file, string target, bool verify)
    {
        if (File.Exists(target)) throw new IOException("the target file already exists");
        file.Refresh();
        long originalSize = file.Length;
        DateTime originalWrite = file.LastWriteTimeUtc;
        for (int attempt = 1; ; attempt++)
        {
            string sourceHash;
            try
            {
                sourceHash = CopyAndHash(file.FullName, target);
                file.Refresh();
                if (file.Length != originalSize || file.LastWriteTimeUtc != originalWrite)
                    throw new IOException("the source changed while it was being copied; retry with the M8 idle");
                if (UsableFileTime(file.CreationTimeUtc)) File.SetCreationTimeUtc(target, file.CreationTimeUtc);
                if (UsableFileTime(file.LastWriteTimeUtc)) File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
                if (!verify || HashFile(target) == sourceHash) return sourceHash;
            }
            catch (Exception)
            {
                TryDelete(target);
                throw;
            }
            TryDelete(target);
            if (attempt == 2)
                throw new IOException("the copy didn't match the original twice; check the USB cable and SD card");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    // Creates `link` as a second name for `existing`. False where hard links aren't supported
    // (FAT, exFAT, most network shares) or fail for any other reason; callers then copy instead.
    static bool TryHardLink(string existing, string link)
    {
        try { return CreateHardLink(link, existing, IntPtr.Zero); }
        catch (Exception) { return false; }
    }

    // NTFS supports hard links; the history store then needs no extra space for file content.
    static bool SupportsHardLinks(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).DriveFormat == "NTFS"; }
        catch (Exception) { return false; }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    static string ToHex(byte[] hash)
    {
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    static string CopyAndHash(string from, string to)
    {
        OperationContext.Current.CheckCancellation();
        using (var sha = SHA256.Create())
        using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                OperationContext.Current.CheckCancellation();
                sha.TransformBlock(buffer, 0, read, null, 0);
                output.Write(buffer, 0, read);
                OperationContext.Current.Progress("Copying", from, input.Position, input.Length);
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            return ToHex(sha.Hash);
        }
    }

    static string HashFile(string path)
    {
        OperationContext.Current.CheckCancellation();
        using (var sha = SHA256.Create())
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                OperationContext.Current.Progress("Hashing", path, input.Position, input.Length);
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            return ToHex(sha.Hash);
        }
    }

    // Returns an error message if the drive holding `path` has less than `needed` bytes free, otherwise null.
    // Network paths can't be checked this way; those are let through.
    static string CheckFreeSpace(string path, long needed)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (needed > free)
                return string.Format("Not enough space on {0}: needs {1}, but only {2} is free.",
                                     root, FormatSize(needed), FormatSize(free));
        }
        catch (Exception) { }
        return null;
    }

    static string FreeSpaceText(string root)
    {
        try { return FormatSize(new DriveInfo(root).AvailableFreeSpace); }
        catch (Exception) { return "unknown"; }
    }

    // "1.2 GB of 7.4 GB"; DriveInfo rejects network paths (\\server\share), which give "unknown".
    static string DriveSpaceText(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return FormatSize(drive.AvailableFreeSpace) + " of " + FormatSize(drive.TotalSize);
        }
        catch (Exception) { return "unknown"; }
    }
}
