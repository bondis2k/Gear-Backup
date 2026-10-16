using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using static GearBackup.PathSafety;
using static GearBackup.SourceScan;

namespace GearBackup
{
    // A standalone full backup: stream the source into a ZIP, then publish the completed archive.
    // No history, content store or second full copy is created.
    internal static class ArchiveBackup
    {
        internal static string FileName(string name, DeviceProfile device, DateTime taken)
        {
            name = BackupSetNames.Validate(name);
            if (name.Length == 0) name = device.Name + "_" + taken.ToString("yyyy-MM-dd_HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.TrimEnd(' ', '.');
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4).TrimEnd(' ', '.');
            if (name.Length == 0) name = "Backup";
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '1' && stem[3] <= '9')) name = "_" + name;
            return name + ".zip";
        }

        internal static OperationResult Run(string source, string destination, Config config, SourceChoice choice = null)
        {
            var context = OperationContext.Current;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            DateTime takenUtc = DateTime.UtcNow;
            source = Rooted(source); destination = Path.GetFullPath(destination);
            RejectRootLinks(source); RejectRootLinks(destination);
            if (!Directory.Exists(source)) throw new IOException("Source folder does not exist: " + source);
            if (Within(destination, source)) throw new IOException("Save the ZIP archive outside the source folder.");
            string parent = Path.GetDirectoryName(destination);
            if (!Directory.Exists(parent)) throw new IOException("Archive destination folder does not exist: " + parent);
            if (Directory.Exists(destination)) throw new IOException("Archive destination is a folder: " + destination);
            var scan = new ScanResult(); SourceScan.CollectFiles(source, scan);
            foreach (var item in Directory.EnumerateFileSystemEntries(source))
                if (string.Equals(Path.GetFileName(item), ArchiveMetadata.ChecksumFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(item), ArchiveMetadata.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The source contains a reserved archive metadata name: " + Path.GetFileName(item) + ". Rename it before creating the ZIP.");
            var actions = new List<string>(); var sizes = new Dictionary<string, long>(); long total = 0;
            foreach (string folder in scan.Folders) actions.Add("Create folder: " + Relative(source, folder));
            foreach (FileInfo file in scan.Files)
            {
                string path = Relative(source, file.FullName); actions.Add("Copy: " + path); sizes[path] = file.Length; total += file.Length;
            }
            OperationOutput.WriteLine("  ZIP archive: " + destination);
            if (!PlanReview.ShowPlan("ZIP archive preview", actions, total, config.Preview, sizes))
                return new OperationResult { Cancelled = !context.PreviewOnly, Summary = context.PreviewOnly ? "ZIP preview complete; no archive created." : "ZIP archive cancelled.", Elapsed = timer.Elapsed };
            var previous = File.Exists(destination) ? new FileInfo(destination) : null;
            long previousSize = previous == null ? 0 : previous.Length, previousTime = previous == null ? 0 : previous.LastWriteTimeUtc.Ticks;
            if (previous != null && !ConsoleUi.AskYesNo("Replace the existing ZIP archive?\n" + destination))
                return new OperationResult { Cancelled = true, Summary = "ZIP archive cancelled; existing archive kept." };
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool created = false;
            var hashes = new Dictionary<string, string>();
            try
            {
                RejectRootLinks(temp);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                    {
                        foreach (string folder in scan.Folders)
                        {
                            context.CheckCancellation(); RejectRootLinks(folder);
                            zip.CreateEntry(Relative(source, folder).Replace('\\', '/') + "/");
                        }
                        var buffer = new byte[65536]; long done = 0; int count = 0;
                        foreach (FileInfo file in scan.Files)
                        {
                            context.CheckCancellation(); RejectRootLinks(file.FullName);
                            string path = Relative(source, file.FullName), name = path.Replace('\\', '/');
                            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                            entry.LastWriteTime = BackupZip.ZipTime(file.LastWriteTime);
                            using (var hash = SHA256.Create())
                            using (var from = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
                            using (var to = entry.Open())
                            {
                                long copied = 0; int read;
                                while ((read = from.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    context.CheckCancellation(); to.Write(buffer, 0, read);
                                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                                    copied += read; done += read;
                                    context.Progress(Stage.Zipping, path, done, total, count, scan.Files.Count);
                                }
                                if (copied != sizes[path]) throw new IOException("Source file changed during archiving: " + path);
                                hash.TransformFinalBlock(new byte[0], 0, 0); hashes.Add(name, FileCopy.ToHex(hash.Hash));
                            }
                            context.Progress(Stage.Zipping, path, done, total, ++count, scan.Files.Count);
                        }
                        context.CheckCancellation();
                        // A placeholder has the same length as the report's eventual SHA-256 checksum.
                        hashes.Add(ArchiveMetadata.FileName, new string('0', 64));
                        int checksumBytes = ArchiveMetadata.Utf8.GetByteCount(ArchiveMetadata.Checksums(hashes));
                        byte[] info = ArchiveMetadata.Utf8.GetBytes(ArchiveMetadata.Create(source, config, choice, takenUtc,
                            scan.Files.Count, scan.Folders.Count, total, checksumBytes));
                        using (var hash = SHA256.Create()) hashes[ArchiveMetadata.FileName] = FileCopy.ToHex(hash.ComputeHash(info));
                        using (var output = zip.CreateEntry(ArchiveMetadata.FileName, CompressionLevel.Fastest).Open()) output.Write(info, 0, info.Length);
                        using (var writer = new StreamWriter(zip.CreateEntry(ArchiveMetadata.ChecksumFileName, CompressionLevel.Fastest).Open(), ArchiveMetadata.Utf8))
                            writer.Write(ArchiveMetadata.Checksums(hashes));
                    }
                }
                if (config.Verify) Verify(temp, hashes);
                context.CheckCancellation(); RejectRootLinks(destination);
                var current = File.Exists(destination) ? new FileInfo(destination) : null;
                if ((current != null) != (previous != null) || (current != null && (current.Length != previousSize || current.LastWriteTimeUtc.Ticks != previousTime)))
                    throw new IOException("The archive destination changed during backup. Choose the destination again.");
                if (File.Exists(destination)) AtomicFile.ReplaceSafely(temp, destination); else File.Move(temp, destination);
                return new OperationResult { Title = "ZIP archive complete", Summary = "Saved full ZIP archive: " + destination,
                    OutputFolder = parent, Counts = new OperationCounts { Copied = scan.Files.Count }, Elapsed = timer.Elapsed };
            }
            finally { if (created) FileCopy.TryDelete(temp); }
        }

        static string Relative(string root, string path) { return path.Substring(root.Length).TrimStart('\\', '/'); }

        static void Verify(string path, Dictionary<string, string> hashes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var buffer = new byte[65536]; int count = 0;
                foreach (var expected in hashes)
                {
                    OperationContext.Current.CheckCancellation();
                    var entry = zip.GetEntry(expected.Key);
                    if (entry == null) throw new IOException("Archive verification failed: " + expected.Key);
                    using (var hash = SHA256.Create())
                    using (var input = entry.Open())
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                        { OperationContext.Current.CheckCancellation(); hash.TransformBlock(buffer, 0, read, buffer, 0); }
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        if (FileCopy.ToHex(hash.Hash) != expected.Value) throw new IOException("Archive verification failed: " + expected.Key);
                    }
                    OperationContext.Current.Progress(Stage.Verifying, expected.Key, 0, 0, ++count, hashes.Count);
                }
            }
        }
    }

}
