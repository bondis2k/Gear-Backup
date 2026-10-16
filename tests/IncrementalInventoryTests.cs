using System;
using System.IO;
using System.Threading;
using static Backups;

partial class Tests
{
    [Test("SQLite: primary incremental inventory and recovery", Folder = "helpers")]
    static void TestIncrementalInventory(string dir)
    {
        foreach (bool single in new[] { true })
        {
            string root = Folder(dir, "primary-inventory-" + single), source = Folder(root, "source"), history = Folder(root, "history");
            Put(Path.Combine(source, "one.txt"), "BASELINE");
            Directory.CreateDirectory(Path.Combine(source, "Empty"));
            var config = new Config { Preview = false };
            string backup = RunBackup(source, history, null, false, config).OutputFolder;
            string database = BackupDatabase.CatalogPath(backup);
            string records = Path.Combine(Snapshots.SnapshotRoot(backup), ContentStore.Current(backup).Name + ".manifest");
            var before = BackupDatabase.Read(backup, true);
            Assert(before.Files.Count == 2 && before.Files.TrueForAll(e => e.SnapshotId == "current"), "incremental query reads only current files and folders");
            OperationResult preview = null;
            TestInteraction ui = new TestInteraction();
            string dbHash = FileCopy.HashFile(database);
            using (var locked = new FileStream(records, FileMode.Open, FileAccess.Read, FileShare.None))
            using (new OperationContext(ui, CancellationToken.None) { PreviewOnly = true }.Activate())
                preview = RunBackup(source, history, backup, true, config);
            Assert(preview.ExitCode == 0 && ui.Output.ToString().Contains("Incremental inventory: SQLite"), "incremental planning uses SQLite even when recovery records cannot be read");
            Assert(FileCopy.HashFile(database) == dbHash, "preview leaves the database unchanged");

            // A prior catalog cannot be trusted after a run starts changing backup contents.
            BackupDatabase.BeginInventoryUpdate(backup);
            ui = WithUi(() => RunBackup(source, history, backup, true, config));
            Assert(ui.Output.ToString().Contains("inventory is stale") && BackupDatabase.TryIncrementalInventory(backup) != null,
                "stale inventory falls back and is refreshed after successful incremental");

            File.WriteAllText(database, "broken database");
            ui = new TestInteraction();
            using (new OperationContext(ui, CancellationToken.None) { PreviewOnly = true }.Activate())
                preview = RunBackup(source, history, backup, true, config);
            Assert(preview.ExitCode == 0 && ui.Output.ToString().Contains("Using recovery records") && File.ReadAllText(database) == "broken database",
                "corrupt inventory preview falls back without repairing or accepting new baselines");
            ui = WithUi(() => RunBackup(source, history, backup, true, config));
            Assert(ui.Output.ToString().Contains("Using recovery records") && BackupDatabase.TryIncrementalInventory(backup) != null,
                "corrupt inventory is rebuilt after successful incremental, including unchanged files");

            File.Delete(database);
            Put(Path.Combine(source, "one.txt"), "NEW CONTENT");
            var result = RunBackup(source, history, backup, true, config);
            Assert(result.ExitCode == 0 && BackupDatabase.Read(backup, true).Files.Find(e => e.Path == "one.txt").Size == 11,
                "missing inventory falls back and records changed content in the new database");

        }
    }
}
