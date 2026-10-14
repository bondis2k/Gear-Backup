using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

partial class Tests
{
    static void TestZipRestore(string root)
    {
        string source = Folder(root, "source"), target = Folder(root, "target"), scratch = Folder(root, "scratch"), archive = Path.Combine(root, "backup.zip");
        Put(Path.Combine(source, "Songs", "one.m8s"), "BACKUP SONG");
        Put(Path.Combine(source, "Samples", "æøå.wav"), "SAMPLE");
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        var config = new Config { Preview = false, BackupFolder = root };
        var detected = new SourceChoice { Path = target, Detected = Devices.M8 };
        WithUi(() => ArchiveBackup.Run(source, archive, config, new SourceChoice { Path = source, Detected = Devices.M8 }));
        ZipRestoreInfo info = null;
        WithUi(() => info = ZipRestore.Inspect(archive));
        Assert(info.Device == Devices.M8 && info.Files == 2 && info.Folders == 3 && info.ContentBytes == 17,
            "ZIP restore inspects identity, membership and actual content size");
        Put(Path.Combine(target, "keep.txt"), "KEEP EXTRA");
        OperationResult result = null;
        WithUi(() => result = ZipRestore.Run(archive, detected, target, config, stagingParent: scratch));
        Assert(result.ExitCode == 0 && result.Counts.Copied == 2 && File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "BACKUP SONG" &&
            File.ReadAllText(Path.Combine(target, "Samples", "æøå.wav")) == "SAMPLE" && Directory.Exists(Path.Combine(target, "Empty")), "ZIP restore copies verified content and empty folders");
        Assert(File.Exists(Path.Combine(target, "keep.txt")) && !File.Exists(Path.Combine(target, ArchiveMetadata.FileName)) &&
            !File.Exists(Path.Combine(target, Checksums.ChecksumFileName)) && Directory.GetFileSystemEntries(scratch).Length == 0,
            "ZIP restore merges, excludes generated metadata and removes extraction files");

        string untouched = Folder(root, "untouched");
        Throws(() => WithUi(() => ZipRestore.Run(archive, null, untouched, config, stagingParent: scratch)), "ZIP restore requires a detected device");
        Throws(() => WithUi(() => ZipRestore.Run(archive, new SourceChoice { Path = untouched, Detected = Devices.OP1Field }, untouched, config, stagingParent: scratch)), "ZIP restore rejects a mismatching device type");
        long required = 0;
        Throws(() => WithUi(() => ZipRestore.Run(archive, detected, untouched, config,
            checkSpace: (path, bytes) => { required = bytes; return "Not enough device space"; }, stagingParent: scratch)), "ZIP restore checks device capacity before extraction or copying");
        Assert(required >= info.ContentBytes && Directory.GetFileSystemEntries(untouched).Length == 0 && Directory.GetFileSystemEntries(scratch).Length == 0,
            "insufficient capacity leaves the device and staging area unchanged");
        Throws(() => WithUi(() => ZipRestore.Run(archive, detected, untouched, config,
            checkSpace: (path, bytes) => PathSafety.Rooted(path) == PathSafety.Rooted(scratch) ? "Not enough temporary space" : null, stagingParent: scratch)), "ZIP restore checks temporary extraction capacity");
        var previewUi = new TestInteraction();
        using (new OperationContext(previewUi, CancellationToken.None) { PreviewOnly = true }.Activate())
            ZipRestore.Run(archive, detected, untouched, config, stagingParent: scratch);
        Assert(previewUi.Plans == 1 && Directory.GetFileSystemEntries(untouched).Length == 0 && Directory.GetFileSystemEntries(scratch).Length == 0,
            "ZIP restore preview verifies the archive without changing the target and cleans its temporary files");
        bool changed = false;
        var changedUi = new TestInteraction { OnReview = p => changed = true };
        using (new OperationContext(changedUi, CancellationToken.None).Activate())
            Throws(() => ZipRestore.Run(archive, detected, untouched, config,
                validateDevice: () => { if (changed) throw new IOException("Device changed during preview"); }, stagingParent: scratch), "ZIP restore rechecks the detected device after preview approval");
        Assert(Directory.GetFileSystemEntries(untouched).Length == 0, "device changes after preview cannot write files");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelledUi = new TestInteraction { OnProgress = p => cancellation.Cancel() };
            bool stopped = false;
            using (new OperationContext(cancelledUi, cancellation.Token).Activate())
                try { ZipRestore.Run(archive, detected, untouched, config, stagingParent: scratch); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped && Directory.GetFileSystemEntries(untouched).Length == 0 && Directory.GetFileSystemEntries(scratch).Length == 0,
                "cancellation during ZIP verification cleans temporary files without changing the target");
        }

        string corrupt = Path.Combine(root, "corrupt.zip"); File.Copy(archive, corrupt);
        ReplaceZipText(corrupt, "Songs/one.m8s", text => "TAMPER SONG");
        Throws(() => WithUi(() => ZipRestore.Run(corrupt, detected, untouched, config, stagingParent: scratch)), "ZIP restore always rejects corrupt file content even with Verify=no");
        Assert(Directory.GetFileSystemEntries(untouched).Length == 0 && Directory.GetFileSystemEntries(scratch).Length == 0, "checksum failure cannot leave partial target content or extraction files");
        File.Copy(archive, corrupt, true);
        ReplaceZipText(corrupt, ArchiveMetadata.FileName, text => text.Replace("M8 (m8)", "M9 (m9)"));
        Throws(() => WithUi(() => ZipRestore.Inspect(corrupt)), "ZIP restore rejects tampered device metadata");

        foreach (string name in new[] { "../escape.txt", "/absolute.txt", "C:/evil.txt", "Songs\\evil.txt", "Songs//evil.txt", "CON.txt", "COM\u00b9.txt", "songs/ONE.m8s", "Songs/one.m8s/child.txt" })
        {
            File.Copy(archive, corrupt, true);
            using (var file = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("EVIL");
            Throws(() => WithUi(() => ZipRestore.Run(corrupt, detected, untouched, config, stagingParent: scratch)), "ZIP restore rejects unsafe, duplicate or conflicting paths: " + name);
        }
        foreach (string name in new[] { ArchiveMetadata.FileName, Checksums.ChecksumFileName })
        {
            File.Copy(archive, corrupt, true);
            using (var file = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Update)) zip.GetEntry(name).Delete();
            Throws(() => WithUi(() => ZipRestore.Inspect(corrupt)), "ZIP restore requires its metadata files: " + name);
        }
        File.Copy(archive, corrupt, true);
        using (var file = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Update)) zip.GetEntry("Songs/one.m8s").ExternalAttributes = unchecked((int)0xa0000000);
        Throws(() => WithUi(() => ZipRestore.Inspect(corrupt)), "ZIP restore rejects symbolic link entries");
        File.Copy(archive, corrupt, true);
        ReplaceZipInfo(corrupt, text => text.Replace("Backup content size (bytes): 17", "Backup content size (bytes): 18"));
        Throws(() => WithUi(() => ZipRestore.Inspect(corrupt)), "ZIP restore derives sizes from entries rather than trusting even checksum-valid metadata");
        File.Copy(archive, corrupt, true);
        ReplaceZipInfo(corrupt, text => text.Replace("M8 (m8)", "M9 (m9)"));
        Throws(() => WithUi(() => ZipRestore.Inspect(corrupt)), "ZIP restore rejects unknown device types even with valid checksums");
        var mtp = new SourceChoice { Detected = Devices.M8, Mtp = new MtpSource { Mirror = source } };
        Throws(() => WithUi(() => ZipRestore.Run(archive, mtp, source, config, stagingParent: scratch)), "MTP ZIP restore cannot write into its mirror");
        WithUi(() => ZipRestore.Run(archive, mtp, untouched, config, stagingParent: scratch));
        Assert(File.Exists(Path.Combine(untouched, "Songs", "one.m8s")), "a matching MTP device can restore ZIP content to a PC folder for manual transfer");
    }

    static void ReplaceZipText(string path, string name, Func<string, string> change)
    {
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(name); string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            entry.Delete();
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(change(text));
        }
    }
    static void ReplaceZipInfo(string path, Func<string, string> change)
    {
        string checksum = null;
        ReplaceZipText(path, ArchiveMetadata.FileName, text => {
            string updated = change(text);
            using (var hash = System.Security.Cryptography.SHA256.Create()) checksum = FileCopy.ToHex(hash.ComputeHash(new UTF8Encoding(false).GetBytes(updated)));
            return updated;
        });
        ReplaceZipText(path, Checksums.ChecksumFileName, text => {
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].EndsWith("  " + ArchiveMetadata.FileName)) lines[i] = checksum + "  " + ArchiveMetadata.FileName;
            return string.Join("\n", lines);
        });
    }
}
