// Comparing files, copying with checksum verification, and free-space checks.
using System;
using System.IO;
using System.Security.Cryptography;

partial class M8Backup
{
    enum FileState { New, Changed, Same }

    // Same size and modified time (within FAT's 2 s resolution) counts as the same file.
    static FileState Compare(FileInfo file, string otherPath)
    {
        var other = new FileInfo(otherPath);
        if (!other.Exists) return FileState.New;
        return other.Length == file.Length &&
               Math.Abs((other.LastWriteTimeUtc - file.LastWriteTimeUtc).TotalSeconds) <= 2
            ? FileState.Same
            : FileState.Changed;
    }

    // Copies a file (target must not exist yet) and returns the SHA-256 checksum of the original.
    // With verify, the copy is read back and compared; a mismatch is retried once, then reported as a failure.
    // A failed copy never leaves a partial file behind.
    static string CopyFile(FileInfo file, string target, bool verify)
    {
        if (File.Exists(target)) throw new IOException("the target file already exists");
        for (int attempt = 1; ; attempt++)
        {
            string sourceHash;
            try
            {
                sourceHash = CopyAndHash(file.FullName, target);
                File.SetCreationTimeUtc(target, file.CreationTimeUtc);
                File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
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
        using (var sha = SHA256.Create())
        using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                output.Write(buffer, 0, read);
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            return ToHex(sha.Hash);
        }
    }

    static string HashFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            return ToHex(sha.ComputeHash(input));
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
}
