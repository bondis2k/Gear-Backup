// Comparing files, copying with checksum verification, and free-space checks.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

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
                OperationContext.Current.Progress(Stage.Comparing, first, a.Position, a.Length);
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
                if (!verify || HashFileFromDisk(target) == sourceHash) return sourceHash;
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

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation info);

    // Identifies the file on disk behind a hard-linked name ("volume:index"), so a check can skip
    // reading the same content twice. Null for a file with only one name, or if it can't be read.
    static string SharedFileId(string path)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                FileInformation info;
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out info) || info.Links < 2) return null;
                return info.VolumeSerial + ":" + (((ulong)info.IndexHigh << 32) | info.IndexLow);
            }
        }
        catch (Exception) { return null; }
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

    const int ChunkSize = 1 << 20;

    // Windows' own SHA-256 (CNG). Measured about 10% faster than SHA256.Create() on a current PC;
    // on older .NET Framework setups, where SHA256.Create() is the managed implementation, the gap is larger.
    static HashAlgorithm NewSha256()
    {
        try { return new SHA256Cng(); }
        catch (Exception) { return SHA256.Create(); }
    }

    static string CopyAndHash(string from, string to)
    {
        OperationContext.Current.CheckCancellation();
        using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            return CopyAndHash(input, output, from);
    }

    // Copies `input` to `output` and returns the SHA-256 of the content. The next chunk is read in the
    // background while the current one is hashed and written, so the card and the backup disk work at
    // the same time instead of taking turns.
    static string CopyAndHash(Stream input, Stream output, string label)
    {
        using (HashAlgorithm sha = NewSha256())
        {
            byte[] current = new byte[ChunkSize], spare = new byte[ChunkSize];
            long total = input.CanSeek ? input.Length : 0, done = 0;
            Task<int> pending = null;
            try
            {
                int read = ReadChunk(input, current);
                while (read > 0)
                {
                    OperationContext.Current.CheckCancellation();
                    byte[] next = spare;
                    pending = Task.Factory.StartNew(() => ReadChunk(input, next));
                    sha.TransformBlock(current, 0, read, null, 0);
                    output.Write(current, 0, read);
                    done += read;
                    OperationContext.Current.Progress(Stage.Copying, label, done, total);
                    read = Result(pending);
                    pending = null;
                    spare = current;
                    current = next;
                }
                sha.TransformFinalBlock(current, 0, 0);
                return ToHex(sha.Hash);
            }
            finally
            {
                // Never leave a read running after the streams close (e.g. when cancelled or a write fails).
                if (pending != null) try { pending.Wait(); } catch (Exception) { }
            }
        }
    }

    // Fills the buffer unless the stream ends first; returns the number of bytes read.
    static int ReadChunk(Stream input, byte[] buffer)
    {
        int count = 0, read;
        while (count < buffer.Length && (read = input.Read(buffer, count, buffer.Length - count)) > 0) count += read;
        return count;
    }

    // A background read's result; its exception is rethrown as itself, not wrapped in an AggregateException.
    static int Result(Task<int> task)
    {
        try { return task.Result; }
        catch (AggregateException ex)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    static string HashFile(string path)
    {
        OperationContext.Current.CheckCancellation();
        using (HashAlgorithm sha = NewSha256())
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                OperationContext.Current.Progress(Stage.Hashing, path, input.Position, input.Length);
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            return ToHex(sha.Hash);
        }
    }

    const uint GenericRead = 0x80000000, ShareRead = 1, ShareDelete = 4, OpenExisting = 3;
    const uint FlagNoBuffering = 0x20000000, FlagSequentialScan = 0x08000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                            uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int toRead, out int read, IntPtr overlapped);

    // Hashes a file as it is on the disk, bypassing Windows' file cache: a read-back right after
    // writing would otherwise come from memory and never see a bad write. Unbuffered reads need a
    // buffer aligned to the disk's sector size (4096 covers 512-byte and 4K sectors). Where the file
    // system refuses them (some network shares), this falls back to a normal read.
    static string HashFileFromDisk(string path)
    {
        OperationContext.Current.CheckCancellation();
        using (SafeFileHandle file = CreateFile(path, GenericRead, ShareRead | ShareDelete, IntPtr.Zero,
                                                OpenExisting, FlagNoBuffering | FlagSequentialScan, IntPtr.Zero))
        {
            if (file.IsInvalid) return HashFile(path);
            IntPtr raw = Marshal.AllocHGlobal(ChunkSize + 4096);
            try
            {
                var aligned = new IntPtr((raw.ToInt64() + 4095) & ~4095L);
                var buffer = new byte[ChunkSize];
                long total = new FileInfo(path).Length, done = 0;
                using (HashAlgorithm sha = NewSha256())
                {
                    while (true)
                    {
                        int read;
                        if (!ReadFile(file, aligned, ChunkSize, out read, IntPtr.Zero)) return HashFile(path);
                        if (read == 0) break;
                        Marshal.Copy(aligned, buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        done += read;
                        OperationContext.Current.Progress(Stage.Verifying, path, done, total);
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    return ToHex(sha.Hash);
                }
            }
            finally { Marshal.FreeHGlobal(raw); }
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
