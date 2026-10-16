using System;
using System.IO;
using static Backups;
using static Session;

partial class Tests
{
    static void TestBackupSetNames(string dir)
    {
        foreach (bool single in new[] { false, true })
        {
            string root = Folder(dir, "named-" + single), source = Folder(root, "source"), history = Folder(root, "history");
            Put(Path.Combine(source, "one.txt"), "first");
            var config = new Config { Preview = false, SingleContentStore = single, FullBackupName = "  Live set — October  " };
            string first = RunBackup(source, history, null, false, config).OutputFolder;
            Assert(BackupSetNames.Read(first) == "Live set — October", "full backup retains trimmed readable name in both storage modes");
            config.FullBackupName = "Studio";
            string second = RunBackup(source, history, first, false, config).OutputFolder;
            Put(Path.Combine(source, "one.txt"), "updated file contents");
            config.IncrementalBackup = first;
            var result = BackupWithLock(source, history, true, config);
            Assert(result.ExitCode == 0 && result.OutputFolder == first, "incremental updates explicitly selected older full set");
            Assert(BackupSetNames.Read(first) == "Live set — October" && BackupSetNames.Read(second) == "Studio", "incremental preserves set names");
            if (single) Assert(ContentStoreBackup.Current(first).Files[0].Size != ContentStoreBackup.Current(second).Files[0].Size, "other full set remains unchanged");
            else Assert(File.ReadAllText(Path.Combine(first, "one.txt")) == "updated file contents" && File.ReadAllText(Path.Combine(second, "one.txt")) == "first", "other full set remains unchanged");
            config.IncrementalBackup = source;
            bool rejected = false;
            try { BackupWithLock(source, history, true, config); } catch (IOException) { rejected = true; }
            Assert(rejected, "selected set outside history is rejected rather than using latest");
            config.IncrementalBackup = Path.Combine(history, "2020-01-01_missing");
            rejected = false;
            try { BackupWithLock(source, history, true, config); } catch (IOException) { rejected = true; }
            Assert(rejected, "missing selection is rejected rather than using latest");
        }
        bool invalid = false;
        try { BackupSetNames.Validate("name\nother"); } catch (ArgumentException) { invalid = true; }
        Assert(invalid, "multiline backup name is rejected");
        string unnamed = Folder(dir, "2026-10-11_unnamed");
        Assert(BackupSetNames.Read(unnamed) == "2026-10-11_unnamed", "old unnamed backups use their directory name");
    }
}
