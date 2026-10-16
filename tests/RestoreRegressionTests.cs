using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static Snapshots;
using static RestoreFlow;
using static DeviceSelection;
using static ProfileSettings;
using static Session;
using static Histories;

partial class Tests
{
    [Test("Current restore preserves dated source names", Folder = "dated-restore")]
    static void TestDatedRestoreNames(string root)
    {
        string source = Folder(root, "source"), history = Path.Combine(root, "history");
        string dated = "take_2026-01-01_120000.wav";
        Put(Path.Combine(source, dated), "REAL SAMPLE");
        Put(Path.Combine(source, "Bundles", "Demo", dated), "BUNDLE SAMPLE");
        Put(Path.Combine(source, "song.txt"), "FIRST");
        var config = new Config { BackupFolder = root, SettingsDirectory = root, Preview = false, Thorough = true };
        var context = new OperationContext(new TestInteraction(), CancellationToken.None);
        new BackupEngine().Run(source, history, null, false, config, context);
        string backup = LatestBackup(history);
        Put(Path.Combine(source, "song.txt"), "SECOND");
        new BackupEngine().Run(source, history, backup, true, config, context);
        var ui = new TestInteraction { OnChooseItem = (q, items) => 1 };
        string target = Folder(root, "all");
        using (new OperationContext(ui, CancellationToken.None).Activate())
            Assert(RunRestore(target, new List<string> { backup }, config).ExitCode == 0, "current-file all restore succeeds");
        Assert(File.ReadAllText(Path.Combine(target, dated)) == "REAL SAMPLE", "all restore keeps a real dated filename");
        Assert(File.ReadAllText(Path.Combine(target, "Bundles", "Demo", dated)) == "BUNDLE SAMPLE", "all restore keeps nested dated source files");

        ui = new TestInteraction { OnChooseItem = (q, items) => 1 };
        ui.Keys.Enqueue(RestoreMode.Bundle.Key);
        target = Folder(root, "bundle");
        using (new OperationContext(ui, CancellationToken.None).Activate())
            Assert(RunRestore(target, new List<string> { backup }, config).ExitCode == 0, "bundle current-file restore succeeds");
        Assert(File.Exists(Path.Combine(target, "Bundles", "Demo", dated)), "bundle restore keeps dated samples");

        ui = new TestInteraction { OnAskText = q => dated, OnChooseItem = (q, items) => q == "Choose a file." ? items.Count : 1 };
        ui.Keys.Enqueue(RestoreMode.SingleFile.Key);
        target = Folder(root, "single");
        using (new OperationContext(ui, CancellationToken.None).Activate())
            Assert(RunRestore(target, new List<string> { backup }, config).ExitCode == 0, "single dated-file restore succeeds");
        Assert(!ui.Questions.Exists(q => q.Contains("old version under")), "a genuine dated source file is never offered a rename");
        Assert(File.Exists(Path.Combine(target, dated)), "single-file restore preserves its exact source name");
    }

    [Test("History selection across configured locations", Folder = "history-locations")]
    static void TestHistoryLocations(string root)
    {
        var config = new Config { BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root };
        string alternate = Path.Combine(root, "alternate");
        SaveProfileSettings(config, "device:tracker-mini", new Dictionary<string, string> { { "BackupFolder", alternate } });
        SaveProfileSettings(config, "profile:Studio", new Dictionary<string, string> { { "BackupFolder", alternate } });
        string globalHistory = Path.Combine(HistoriesRoot(config), "profiles", "Studio");
        string otherHistory = Path.Combine(HistoriesRoot(new Config { BackupFolder = alternate }), "profiles", "Studio");
        Directory.CreateDirectory(Path.Combine(globalHistory, "2026-01-01"));
        Directory.CreateDirectory(Path.Combine(otherHistory, "2026-01-01"));
        int before;
        Assert(AvailableHistories(config).Count == 2, "overlapping override locations are deduplicated without losing same-name profiles");
        var ui = new TestInteraction { OnChooseItem = (q, items) => {
            Assert(items.Count == 2 && items[0] != items[1], "same-name histories have distinguishable location labels");
            return new List<string>(items).FindIndex(label => label.Contains(otherHistory)) + 1;
        } };
        using (new OperationContext(ui, CancellationToken.None).Activate())
        {
            Assert(HistoryToCheck(config) == otherHistory, "unnamed integrity checks can select an override location");
            Assert(ChooseHistory(config, globalHistory) == otherHistory, "restore selection includes other configured locations");
            ui.OnChooseItem = (q, items) => 0;
            Assert(ChooseHistory(config, globalHistory) == null, "cancelling the cross-location picker changes nothing");
            Config named = config.Clone(); named.Profile = "Studio";
            Assert(HistoryToCheck(named) == null, "duplicate named profiles still allow cancellation of location selection");
            before = AvailableHistories(config).Count;
            Directory.CreateDirectory(Path.Combine(HistoriesRoot(config), "profiles", "Later", "2026-02-01"));
            Assert(AvailableHistories(config).Count == before, "an operation looks through its backup locations only once");
            AvailableHistories(config).Clear();
            Assert(AvailableHistories(config).Count == before, "callers can't change the remembered histories");
        }
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            Assert(AvailableHistories(config).Count == before + 1, "the next operation sees new histories");
        Directory.Delete(Path.Combine(globalHistory, "2026-01-01"));
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            Assert(HasHistory(config), "CLI discovers histories outside an empty global location");
            Config only = config.Clone(); only.Profile = "Studio";
            Assert(HistoryToCheck(only) == otherHistory, "a named profile with one populated location is selected directly");
        }
    }

    [Test("Restore uses saved device and preferences", Folder = "restore-device")]
    static void TestRestoreDevicePreferences(string root)
    {
        var global = new Config { BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root, Preview = false };
        string alternate = Path.Combine(root, "tracker"), recovery = Path.Combine(root, "recovery");
        SaveProfileSettings(global, "device:tracker-mini", new Dictionary<string, string> {
            { "BackupFolder", alternate }, { "Recovery", "yes" }, { "RecoveryFolder", recovery }, { "Verify", "yes" }, { "Thorough", "yes" }
        });
        string source = Folder(root, "source"), relative = "Projects\\Group\\Demo\\project.mt";
        Put(Path.Combine(source, relative), "PROJECT");
        Config backupConfig = ResolveProfileSettings(global, Devices.TrackerMini.Id, "Studio");
        string history = Path.Combine(HistoriesRoot(backupConfig), "profiles", "Studio");
        new BackupEngine().Run(source, history, null, false, backupConfig,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.TrackerMini });
        string backup = LatestBackup(history);
        Assert(BackupDevice(backup) == Devices.TrackerMini, "named backups persist their device type");
        string target = Folder(root, "target"); Put(Path.Combine(target, relative), "ORIGINAL");
        var ui = new TestInteraction { OnChooseItem = (q, items) => 1 };
        ui.Keys.Enqueue(RestoreMode.ProjectFolder.Key); ui.Keys.Enqueue('r');
        using (new OperationContext(ui, CancellationToken.None).Activate())
        {
            PreparedSource prepared = PrepareSource(global, new SourceChoice { Path = target });
            Assert(prepared.Device == Devices.M8, "unidentified target starts with the default device");
            Assert(RunPrepared(prepared, true, false).ExitCode == 0, "shared restore selects a named tracker backup outside the global location");
            Assert(CurrentDevice == Devices.M8, "restore restores its caller's device context afterward");
        }
        Assert(File.ReadAllText(Path.Combine(target, relative)) == "PROJECT", "whole-project restore copies the selected tracker project");
        Assert(ui.Options.Exists(options => new List<Option>(options).Exists(o => o.Key == RestoreMode.ProjectFolder.Key)), "saved device supplies whole-project choices");
        string[] recovered = Directory.GetFiles(recovery, "project.mt", SearchOption.AllDirectories);
        Assert(recovered.Length == 1 && File.ReadAllText(recovered[0]) == "ORIGINAL", "saved device recovery preferences preserve the original target");

        string storedProject = Snapshots.CurrentBackup(backup).Files.Find(e => e.Relative == relative).StoredPath;
        Put(storedProject, "DAMAGED");
        ui = new TestInteraction { OnChooseItem = (q, items) => 1 };
        using (new OperationContext(ui, CancellationToken.None).Activate())
        {
            PreparedSource prepared = PrepareSource(global, new SourceChoice { Path = target });
            Throws(() => RunPrepared(prepared, true, false), "saved-device Verify preference rejects same-size checksum damage");
            Assert(CurrentDevice == Devices.M8, "failure also restores the caller's device context");
        }
        Assert(File.ReadAllText(Path.Combine(target, relative)) == "PROJECT", "failed verification leaves the restored target unchanged");
        string automatic = Path.Combine(HistoriesRoot(global), "profiles", "tracker-mini-card-ABCDEF12", "2026-01-01");
        Directory.CreateDirectory(automatic);
        Assert(BackupDevice(automatic) == null, "automatic history names do not replace missing device records");
        Put(Path.Combine(AppIdentity.MetadataRoot(backup), "device.txt"), "unknown-device");
        Assert(BackupDevice(backup) == null, "a device this version doesn't know counts as no record");
        Put(Path.Combine(AppIdentity.MetadataRoot(automatic), "device.txt"), "unknown-device");
        Assert(BackupDevice(automatic) == null, "an unknown device record is not inferred from its folder name");
        ui = new TestInteraction { OnChooseItem = (q, items) => 1 };
        ui.Keys.Enqueue(RestoreMode.All.Key); ui.Keys.Enqueue('r');
        using (new OperationContext(ui, CancellationToken.None).Activate())
        {
            PreparedSource prepared = PrepareSource(global, new SourceChoice { Path = target });
            Put(storedProject, "PROJECT");
            Throws(() => RunPrepared(prepared, true, false), "restore refuses a backup with invalid device identity");
        }
        Put(Path.Combine(AppIdentity.MetadataRoot(backup), "device.txt"), new string('x', 200));
        Throws(() => BackupDevice(backup), "an oversized device record is refused");
    }
}
