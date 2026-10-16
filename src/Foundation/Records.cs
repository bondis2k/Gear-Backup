using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static GearBackup.PathSafety;

namespace GearBackup
{
    // Reading the files the program keeps beside backups and settings: status and check records, device
    // records, checksum lists, snapshot manifests, the old-version record, statistics and settings files.
    // Every read goes through here so each is checked the same way, without callers having to remember:
    // the file and the folders above it must not be links (which could redirect the read anywhere), and
    // the file must be within a size limit far above what the program writes, so a damaged or planted
    // file is reported instead of being read whole into memory. Writes go through AtomicFile.AtomicText,
    // which checks links the same way.
    static class Records
    {
        internal const long DeviceLimit = 128, SmallLimit = 4 * 1024, SettingsLimit = 1024 * 1024,
            OldVersionLimit = 16L * 1024 * 1024, ListLimit = 128L * 1024 * 1024;

        internal static string ReadText(string path, long limit, string what)
        {
            using (var input = Open(path, limit, what))
            using (var reader = new StreamReader(input, Encoding.UTF8, true)) return reader.ReadToEnd();
        }

        internal static string[] ReadLines(string path, long limit, string what)
        {
            using (var input = Open(path, limit, what))
            using (var reader = new StreamReader(input, Encoding.UTF8, true))
            {
                var lines = new List<string>(); string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines.ToArray();
            }
        }

        internal static byte[] ReadBytes(string path, long limit, string what)
        {
            using (var input = Open(path, limit, what))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[65536]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0) output.Write(buffer, 0, read);
                return output.ToArray();
            }
        }

        static Stream Open(string path, long limit, string what)
        {
            RejectRootLinks(path);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                RejectRootLinks(path);
                if (file.Length > limit) throw new IOException(what + " is too large: " + path);
                return new LimitedReadStream(file, limit, what + " is too large: " + path);
            }
            catch { file.Dispose(); throw; }
        }
    }

    // The limit applies to bytes actually read from the same open handle, not a prior pathname stat.
    // Used with StreamReader too, so BOM detection and line parsing retain their normal behavior.
    internal sealed class LimitedReadStream : Stream
    {
        readonly Stream input;
        readonly long limit;
        readonly string error;
        long consumed;
        internal LimitedReadStream(Stream input, long limit, string error)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException("limit");
            this.input = input; this.limit = limit; this.error = error;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            OperationContext active = OperationContext.Active;
            if (active != null) active.CheckCancellation();
            long remaining = limit - consumed;
            // Read one byte past the budget to distinguish exact EOF from oversize input.
            int wanted = remaining < count ? (int)remaining + 1 : count;
            int read = input.Read(buffer, offset, wanted);
            if (read > remaining) throw new IOException(error);
            consumed += read; return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        public override bool CanRead { get { return input.CanRead; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { return consumed; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

}
