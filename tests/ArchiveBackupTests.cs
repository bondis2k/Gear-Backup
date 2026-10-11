using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

partial class Tests
{
    static void TestArchiveBackup(string root)
    {
        string source = Folder(root, "source"), output = Path.Combine(root, "standalone.zip");
        Put(Path.Combine(source, "Songs", "one.m8s"), "ARCHIVE SONG");
        Put(Path.Combine(source, "Samples", "kick.wav"), "ARCHIVE SAMPLE");
        Put(Path.Combine(source, "Thumbs.db"), "SKIPPED");
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        var config = new Config { BackupFolder = Path.Combine(source, "unused-backups"), Verify = true, Preview = false };
        Assert(ArchiveBackup.FileName("", Devices.M8, new DateTime(2026, 10, 11, 12, 34, 56)) == "M8_2026-10-11_12.34.56.zip",
            "blank archive name uses device plus date and time");
        Assert(ArchiveBackup.FileName("Live set", Devices.OP1Field, DateTime.Now) == "Live set.zip" &&
            ArchiveBackup.FileName("Live.zip", Devices.M8, DateTime.Now) == "Live.zip", "custom archive names are used without a second extension");
        Assert(ArchiveBackup.FileName("../Live:jam", Devices.M8, DateTime.Now) == ".._Live_jam.zip" &&
            ArchiveBackup.FileName("CON", Devices.M8, DateTime.Now) == "_CON.zip", "suggested archive names cannot introduce path separators or reserved Windows names");
        OperationResult result = null;
        var ui = WithUi(() => result = ArchiveBackup.Run(source, output, config));
        Assert(result.ExitCode == 0 && result.Counts.Copied == 2 && result.OutputFolder == root && !Directory.Exists(config.BackupFolder),
            "archive backup publishes only a ZIP without creating a history or full backup folder");
        using (var zip = new ZipArchive(File.OpenRead(output), ZipArchiveMode.Read))
        {
            Assert(zip.Entries.Count == 5 && zip.GetEntry("Empty/") != null && zip.GetEntry("Thumbs.db") == null,
                "archive includes files and empty folders with normal backup exclusions");
            using (var reader = new StreamReader(zip.GetEntry("Songs/one.m8s").Open()))
                Assert(reader.ReadToEnd() == "ARCHIVE SONG", "ZIP entries preserve the source contents");
        }
        Assert(ui.Progress.Exists(p => p.Stage == Stage.Verifying), "optional archive verification reads back every file entry");
        string originalHash = FileCopy.HashFile(output);
        WithUi(() => result = ArchiveBackup.Run(source, output, config), 'n');
        Assert(result.Cancelled && FileCopy.HashFile(output) == originalHash, "declining replacement preserves the existing ZIP");
        string preview = Path.Combine(root, "preview.zip");
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true }.Activate())
            result = ArchiveBackup.Run(source, preview, config);
        Assert(!File.Exists(preview) && result.Counts == null, "archive preview creates no ZIP or temporary files");
        Throws(() => WithUi(() => ArchiveBackup.Run(source, Path.Combine(source, "self.zip"), config)), "archive destination cannot be inside the source");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelledUi = new TestInteraction { OnProgress = p => { if (p.Stage == Stage.Zipping) cancellation.Cancel(); } };
            bool stopped = false;
            using (new OperationContext(cancelledUi, cancellation.Token).Activate())
                try { ArchiveBackup.Run(source, output, config); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "cancellation interrupts archive writing");
        }
        Assert(FileCopy.HashFile(output) == originalHash && Directory.GetFiles(root, "*.tmp").Length == 0,
            "cancellation preserves the existing archive and removes its incomplete temporary output");
        Put(Path.Combine(source, "Songs", "one.m8s"), "UPDATED ARCHIVE SONG");
        WithUi(() => ArchiveBackup.Run(source, output, config), 'y');
        Assert(FileCopy.HashFile(output) != originalHash, "confirmed replacement publishes the new completed archive");
        var changedUi = new TestInteraction { OnProgress = p => { if (p.Stage == Stage.Zipping && !File.Exists(preview)) Put(preview, "OTHER WRITER"); } };
        using (new OperationContext(changedUi, CancellationToken.None).Activate())
            Throws(() => ArchiveBackup.Run(source, preview, config), "a destination created by another writer is not silently replaced");
        Assert(File.ReadAllText(preview) == "OTHER WRITER" && Directory.GetFiles(root, "*.tmp").Length == 0,
            "destination races preserve the other file and clean incomplete output");
    }
}
