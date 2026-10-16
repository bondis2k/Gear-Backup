// Comparing files, copying with checksum verification, and free-space checks.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using static ConsoleUi;
using static Formatting;
using static DeviceSelection;
using static FileCopy;

static class FileCopy
{
    // Same size and modified time (within FAT's 2 s resolution) counts as the same file.
    internal static FileState Compare(FileInfo file, string otherPath)
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

    internal static bool UsableFileTime(DateTime time)
    {
        return time >= new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    // Exact comparison for missing timestamps; no SHA-256 calculation/comparison.
    internal static bool FilesEqual(string first, string second)
    {
        using (var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (a.Length != b.Length) return false;
            byte[] left = new byte[65536], right = new byte[65536];
            int count;
            while ((count = ReadChunk(a, left)) > 0)
            {
                OperationContext.Current.Progress(Stage.Comparing, first, a.Position, a.Length);
                if (ReadChunk(b, right) != count) return false;
                for (int i = 0; i < count; i++) if (left[i] != right[i]) return false;
            }
            return b.ReadByte() == -1;
        }
    }

    internal static FileState CompareContent(FileInfo file, string target, bool thorough)
    {
        string sourceHash;
        return CompareContent(file, target, thorough, out sourceHash);
    }

    // Thorough: compare SHA-256 of both files (sourceHash returns the source's, to reuse). Otherwise
    // size and modified time, as Compare does.
    internal static FileState CompareContent(FileInfo file, string target, bool thorough, out string sourceHash)
    {
        sourceHash = null;
        if (!thorough) return Compare(file, target);
        var other = new FileInfo(target);
        if (!other.Exists) return FileState.New;
        if (other.Length != file.Length) return FileState.Changed;
        sourceHash = HashFile(file.FullName);
        return sourceHash == HashFile(target) ? FileState.Same : FileState.Changed;
    }

    // Copies a file (target must not exist yet) and returns the SHA-256 checksum of the original.
    // With verify, the copy is read back and compared; a mismatch is retried once, then reported as a failure.
    // A failed copy never leaves a partial file behind.
    internal static string CopyFile(FileInfo file, string target, bool verify)
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
                    throw new IOException("the source changed while it was being copied; retry with the " + CurrentDevice.Name + " idle");
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
    internal static bool TryHardLink(string existing, string link)
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
    internal static string SharedFileId(string path)
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
    internal static bool SupportsHardLinks(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).DriveFormat == "NTFS"; }
        catch (Exception) { return false; }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    internal static string ToHex(byte[] hash)
    {
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    internal const int ChunkSize = 1 << 20;

    // Windows' own SHA-256 (CNG). Measured about 10% faster than SHA256.Create() on a current PC;
    // on older .NET Framework setups, where SHA256.Create() is the managed implementation, the gap is larger.
    static HashAlgorithm NewSha256()
    {
        try { return new SHA256Cng(); }
        catch (Exception) { return SHA256.Create(); }
    }

    internal static string CopyAndHash(string from, string to)
    {
        OperationContext.Current.CheckCancellation();
        using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            return CopyAndHash(input, output, from);
    }

    // Copies `input` to `output` and returns the SHA-256 of the content. The next chunk is read in the
    // background while the current one is hashed and written, so the card and the backup disk work at
    // the same time instead of taking turns.
    internal static string CopyAndHash(Stream input, Stream output, string label)
    {
        using (HashAlgorithm sha = NewSha256())
        {
            long total = input.CanSeek ? input.Length : 0, done = 0;
            bool small = input.CanSeek && input.Length - input.Position <= 65536;
            OperationContext context = OperationContext.Current;
            byte[] current = context.RentBuffer(small), spare = small ? null : context.RentBuffer(false);
            Task<int> pending = null;
            try
            {
                int read = ReadChunk(input, current);
                while (read > 0)
                {
                    OperationContext.Current.CheckCancellation();
                    byte[] next = spare;
                    if (!small) pending = Task.Factory.StartNew(() => ReadChunk(input, next));
                    sha.TransformBlock(current, 0, read, null, 0);
                    output.Write(current, 0, read);
                    done += read;
                    OperationContext.Current.Progress(Stage.Copying, label, done, total);
                    read = small ? ReadChunk(input, current) : Result(pending);
                    pending = null;
                    if (!small) { spare = current; current = next; }
                }
                sha.TransformFinalBlock(current, 0, 0);
                return ToHex(sha.Hash);
            }
            finally
            {
                // Never leave a read running after the streams close (e.g. when cancelled or a write fails).
                if (pending != null) try { pending.Wait(); } catch (Exception) { }
                context.ReturnBuffer(current);
                context.ReturnBuffer(spare);
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

    internal static string HashFile(string path)
    {
        OperationContext.Current.CheckCancellation();
        using (HashAlgorithm sha = NewSha256())
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        {
            OperationContext context = OperationContext.Current;
            byte[] buffer = context.RentBuffer(input.Length <= 65536);
            try
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    context.Progress(Stage.Hashing, path, input.Position, input.Length);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return ToHex(sha.Hash);
            }
            finally { context.ReturnBuffer(buffer); }
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
    // Unbuffered reads get no read-ahead from Windows, so the next chunk is read in the background while
    // the current one is hashed: measured 1.4-2.2x faster than taking turns (2 GB file on an NVMe SSD).
    internal static string HashFileFromDisk(string path)
    {
        OperationContext.Current.CheckCancellation();
        using (SafeFileHandle file = CreateFile(path, GenericRead, ShareRead | ShareDelete, IntPtr.Zero,
                                                OpenExisting, FlagNoBuffering | FlagSequentialScan, IntPtr.Zero))
        {
            if (file.IsInvalid) return HashFile(path);
            long total = new FileInfo(path).Length, done = 0;
            bool small = total <= 65536;
            int readSize = small ? 65536 : ChunkSize;
            OperationContext context = OperationContext.Current;
            byte[] buffer = context.RentBuffer(small);
            IntPtr rawCurrent = IntPtr.Zero, rawSpare = IntPtr.Zero;
            Task<int> pending = null;
            try
            {
                rawCurrent = Marshal.AllocHGlobal(readSize + 4096);
                if (!small) rawSpare = Marshal.AllocHGlobal(readSize + 4096);
                IntPtr current = Aligned(rawCurrent), spare = Aligned(rawSpare);
                using (HashAlgorithm sha = NewSha256())
                {
                    int read = ReadUnbuffered(file, current, readSize);
                    while (read > 0)
                    {
                        IntPtr next = spare;
                        if (!small) pending = Task.Factory.StartNew(() => ReadUnbuffered(file, next, readSize));
                        Marshal.Copy(current, buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        done += read;
                        OperationContext.Current.Progress(Stage.Verifying, path, done, total);
                        read = small ? ReadUnbuffered(file, current, readSize) : Result(pending);
                        pending = null;
                        if (!small) { spare = current; current = next; }
                    }
                    if (read < 0) return HashFile(path);
                    sha.TransformFinalBlock(buffer, 0, 0);
                    return ToHex(sha.Hash);
                }
            }
            finally
            {
                // Never free a buffer, or close the file, while a background read still uses it.
                if (pending != null) try { pending.Wait(); } catch (Exception) { }
                Marshal.FreeHGlobal(rawCurrent);
                Marshal.FreeHGlobal(rawSpare);
                context.ReturnBuffer(buffer);
            }
        }
    }

    static IntPtr Aligned(IntPtr raw) { return new IntPtr((raw.ToInt64() + 4095) & ~4095L); }

    // One unbuffered chunk: the bytes read, 0 at the end of the file, or -1 when Windows refuses the read.
    static int ReadUnbuffered(SafeFileHandle file, IntPtr buffer, int readSize)
    {
        int read;
        return ReadFile(file, buffer, readSize, out read, IntPtr.Zero) ? read : -1;
    }

    // Returns an error message if the drive holding `path` has less than `needed` bytes free, otherwise null.
    // Network paths can't be checked this way; those are let through.
    internal static string CheckFreeSpace(string path, long needed)
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

    internal static string FreeSpaceText(string root)
    {
        try { return FormatSize(new DriveInfo(root).AvailableFreeSpace); }
        catch (Exception) { return "unknown"; }
    }

    // "1.2 GB of 7.4 GB"; DriveInfo rejects network paths (\\server\share), which give "unknown".
    internal static string DriveSpaceText(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return FormatSize(drive.AvailableFreeSpace) + " of " + FormatSize(drive.TotalSize);
        }
        catch (Exception) { return "unknown"; }
    }
}

internal enum FileState { New, Changed, Same }
