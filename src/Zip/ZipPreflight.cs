using System;
using System.IO;

namespace GearBackup
{
    // ZipArchive materializes its central directory on the first Entries access. Validate and bound the
    // actual records first, on the same seekable, read-locked file handle that ZipArchive will use.
    internal static class ZipPreflight
    {
        internal const long MaxDirectoryBytes = 32L * 1024 * 1024;
        internal const int MaxNameBytes = 1024, MaxExtraBytes = 4096, MaxCommentBytes = 4096;

        internal static void Validate(Stream file, int maxEntries, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            context.CheckCancellation();
            ulong count, size, offset;
            long boundary;
            LocateDirectory(file, context, out count, out size, out offset, out boundary);
            if (maxEntries < 0 || count > (ulong)maxEntries) throw new IOException("ZIP entry count exceeds the restore limit.");
            if (size > (ulong)MaxDirectoryBytes) throw new IOException("ZIP central directory exceeds the memory budget.");
            if (offset > (ulong)boundary || size != (ulong)boundary - offset) throw Invalid();
            WalkDirectory(file, count, offset, boundary, maxEntries, context);
            file.Position = 0;
        }

        // Finds the end-of-central-directory record (and the ZIP64 one when present) and returns where the directory is.
        static void LocateDirectory(Stream file, OperationContext context, out ulong count, out ulong size, out ulong offset, out long boundary)
        {
            long length = file.Length;
            if (length < 22) throw Invalid();
            // Only the EOCD and its bounded comment can follow the central directory.
            int tailLength = (int)Math.Min(length, 65535 + 22);
            var tail = new byte[tailLength];
            file.Position = length - tailLength; ReadExactly(file, tail, tail.Length, context);
            int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
                if (U32(tail, i) == 0x06054b50) { end = i; break; }
            if (end < 0 || end + 22 + U16(tail, end + 20) != tail.Length ||
                U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0) throw Invalid();
            long endOffset = length - tailLength + end;
            boundary = endOffset;
            count = U16(tail, end + 10); size = U32(tail, end + 12); offset = U32(tail, end + 16);
            if (U16(tail, end + 8) != count) throw Invalid();
            bool needs64 = count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue;
            byte[] locator = new byte[20];
            bool has64 = false;
            if (endOffset >= 20)
            {
                file.Position = endOffset - 20; ReadExactly(file, locator, locator.Length, context);
                has64 = U32(locator, 0) == 0x07064b50;
            }
            if (needs64 && !has64) throw Invalid();
            if (has64) ApplyZip64(file, locator, endOffset, context, ref count, ref size, ref offset, ref boundary);
        }

        static void ApplyZip64(Stream file, byte[] locator, long endOffset, OperationContext context, ref ulong count, ref ulong size, ref ulong offset, ref long boundary)
        {
            if (U32(locator, 4) != 0 || U32(locator, 16) != 1) throw Invalid();
            ulong recordOffset = U64(locator, 8);
            if (recordOffset > (ulong)Math.Max(0, endOffset - 20 - 56)) throw Invalid();
            var record = new byte[56]; file.Position = (long)recordOffset; ReadExactly(file, record, record.Length, context);
            ulong recordSize = U64(record, 4);
            if (U32(record, 0) != 0x06064b50 || recordSize < 44 || recordSize > 65536 ||
                recordOffset + 12 + recordSize != (ulong)(endOffset - 20) || U32(record, 16) != 0 || U32(record, 20) != 0 ||
                U64(record, 24) != U64(record, 32)) throw Invalid();
            ulong count64 = U64(record, 32), size64 = U64(record, 40), offset64 = U64(record, 48);
            if ((count != ushort.MaxValue && count != count64) || (size != uint.MaxValue && size != size64) ||
                (offset != uint.MaxValue && offset != offset64)) throw Invalid();
            count = count64; size = size64; offset = offset64; boundary = (long)recordOffset;
        }

        // Reads every central-directory header, bounding the entry count and the name, extra and comment sizes.
        static void WalkDirectory(Stream file, ulong count, ulong offset, long directoryEnd, int maxEntries, OperationContext context)
        {
            file.Position = (long)offset;
            var header = new byte[46];
            ulong actual = 0;
            while (file.Position < directoryEnd)
            {
                context.CheckCancellation();
                if (++actual > (ulong)maxEntries || actual > count) throw new IOException("ZIP contains more entries than declared or allowed.");
                if (directoryEnd - file.Position < header.Length) throw Invalid();
                ReadExactly(file, header, header.Length, context);
                if (U32(header, 0) != 0x02014b50 || U16(header, 34) != 0) throw Invalid();
                int name = U16(header, 28), extra = U16(header, 30), comment = U16(header, 32);
                if (name == 0 || name > MaxNameBytes || extra > MaxExtraBytes || comment > MaxCommentBytes)
                    throw new IOException("ZIP entry name, extra data or comment exceeds the restore limit.");
                int skip = name + extra + comment;
                if (skip > directoryEnd - file.Position) throw Invalid();
                file.Position += skip;
            }
            if (actual != count) throw new IOException("ZIP entry count does not match its central directory.");
        }

        static void ReadExactly(Stream input, byte[] bytes, int count, OperationContext context)
        {
            int done = 0, read;
            while (done < count)
            {
                context.CheckCancellation();
                read = input.Read(bytes, done, count - done);
                if (read == 0) throw Invalid();
                done += read;
            }
        }
        static ushort U16(byte[] b, int i) { return (ushort)(b[i] | b[i + 1] << 8); }
        static uint U32(byte[] b, int i) { return (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24); }
        static ulong U64(byte[] b, int i) { return U32(b, i) | (ulong)U32(b, i + 4) << 32; }
        static IOException Invalid() { return new IOException("Invalid or unsupported ZIP central directory."); }
    }
}
