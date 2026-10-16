using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using static GearBackup.FileCopy;
using static GearBackup.PathSafety;
using static GearBackup.Snapshots;
using static GearBackup.Backups;
using static GearBackup.FujiRecipes;
using static GearBackup.FujiSettings;
using static GearBackup.MtpMirror;
using static GearBackup.StorageCatalog;
using static GearBackup.AppSettings;
using static GearBackup.ProfileSettings;
using static GearBackup.Session;
using static GearBackup.BackupStatuses;
using static GearBackup.Histories;
using static GearBackup.ProfileNaming;
using static GearBackup.HistoryLock;
using static GearBackup.BackupFolders;
using static GearBackup.FujiSettingsBackup;
using GearBackup;
using GearBackup.Cli;

partial class Tests
{
    [Test("Backup destination defaults and creation", Folder = "destination")]
    static void TestBackupDestination(string root)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Backup");
        Assert(new Config { }.BackupFolder == expected, "new configurations use the user profile Backup folder");
        Config loaded = null;
        WithUi(() => loaded = LoadConfig(root));
        Assert(loaded.BackupFolder == expected, "a new INI uses the user profile Backup folder");
        Put(Path.Combine(root, AppIdentity.ConfigFileName), "BackupFolder=\n");
        WithUi(() => loaded = LoadConfig(root));
        Assert(loaded.BackupFolder == expected, "an empty INI backup location uses the same default");
        string source = Folder(root, "source"); Put(Path.Combine(source, "song.txt"), "SONG");
        var config = new Config { BackupFolder = Path.Combine(root, "missing", "backups"), SettingsDirectory = root, Preview = false };
        PreparedSource prepared = null;
        OperationResult result = null;
        TestInteraction declined = WithUi(() => {
            prepared = PrepareSource(config, new SourceChoice { Path = source });
            result = RunPrepared(prepared, false, false);
        }, 'n');
        Assert(result.Cancelled && !Directory.Exists(config.BackupFolder) && !Directory.Exists(prepared.History),
            "declining folder creation cancels without creating destination or history");
        Assert(declined.Questions.Count == 1 && declined.Questions[0].Contains(config.BackupFolder),
            "creation prompt identifies the selected destination");
        TestInteraction accepted = WithUi(() => result = RunPrepared(prepared, false, false), 'y');
        Assert(!result.Cancelled && result.ExitCode == 0 && LatestBackup(prepared.History) != null,
            "accepting creates missing parent folders and completes the backup");
        Assert(accepted.Questions.Count == 1, "missing destination prompts exactly once");
        TestInteraction existing = WithUi(() => result = RunPrepared(prepared, false, true));
        Assert(existing.Questions.Count == 0, "an existing destination does not prompt for creation");
        config.BackupFolder = Path.Combine(root, "preview-only");
        var preview = new TestInteraction();
        using (new OperationContext(preview, CancellationToken.None) { PreviewOnly = true }.Activate())
        {
            prepared = PrepareSource(config, new SourceChoice { Path = source });
            RunPrepared(prepared, false, false);
        }
        Assert(preview.Questions.Count == 0 && !Directory.Exists(config.BackupFolder), "preview neither prompts nor creates a destination");
        Put(config.BackupFolder, "FILE");
        WithUi(() => Throws(() => EnsureBackupFolder(config), "a file cannot be used as a backup folder"));
        config.BackupFolder = Path.Combine(root, "settings-cancelled");
        WithUi(() => result = SaveFujiSettings(new Dictionary<string, byte[]>(), Path.Combine(config.BackupFolder, "history"), config), 'n');
        Assert(result.Cancelled && !Directory.Exists(config.BackupFolder), "camera settings backup also honors declined creation");
    }

    [Test("Device and profile preferences", Folder = "profile-preferences")]
    static void TestProfilePreferences(string root)
    {
        var global = new Config { BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root };
        WithUi(() => {
            SaveProfileSettings(global, "device:m8", new Dictionary<string, string> { { "BackupFolder", Path.Combine(root, "device-store") }, { "Thorough", "yes" }, { "KeepBackups", "3" } });
            SaveProfileSettings(global, "profile:Studio", new Dictionary<string, string> { { "KeepBackups", "5" }, { "Verify", "yes" } });
        });
        Config resolved = ResolveProfileSettings(global, "m8", "Studio");
        Assert(resolved.Thorough && resolved.Verify && resolved.KeepBackups == 5 && resolved.BackupFolder == Path.Combine(root, "device-store"), "profile preferences override device defaults while inheriting other preferences");
        Assert(!global.Thorough && !global.Verify && global.KeepBackups == 0, "resolving preferences never mutates global settings");
        Config other = ResolveProfileSettings(global, "x100vi", "Other");
        Assert(!other.Thorough && !other.Verify && other.BackupFolder == global.BackupFolder, "unrelated devices and profiles keep global preferences");
        Assert(ReadProfileSettings(global).Count == 2 && ConfiguredBackupLocations(global).Count == 2, "override settings round-trip and custom locations are discoverable");
        WithUi(() => SaveProfileSettings(global, "profile:Studio", new Dictionary<string, string>()));
        Assert(ResolveProfileSettings(global, "m8", "Studio").KeepBackups == 3 && ReadProfileSettings(global).ContainsKey("device:m8"), "resetting a profile preserves device preferences and restores inheritance");
        Throws(() => SaveProfileSettings(global, "profile:..", new Dictionary<string, string>()), "unsafe profile scopes are refused");
        Throws(() => SaveProfileSettings(global, "device:unknown", new Dictionary<string, string>()), "unknown devices are refused");
        Throws(() => SaveProfileSettings(global, "profile:Studio", new Dictionary<string, string> { { "SourceFolder", root } }), "source identity cannot be overridden by preferences");
        Throws(() => SaveProfileSettings(global, "profile:Studio", new Dictionary<string, string> { { "BackupFolder", "x\n[profile:Other]" } }), "INI section injection is refused");
        string source = Rooted(Path.Combine(root, "source")); Put(Path.Combine(source, "song.txt"), "PROFILE");
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.M8, PreviewOnly = true }.Activate())
        {
            resolved = ResolveSourceSettings(global, source);
            Assert(resolved.Thorough && HistoryFor(resolved, source).StartsWith(Path.Combine(root, "device-store")), "automatic profiles use device preferences before selecting their destination");
        }
        Assert(!Directory.Exists(global.BackupFolder) && !Directory.Exists(resolved.BackupFolder), "resolving preview preferences creates no backup folders");
    }
    static BackupCatalog ManagementFixture(string root, out Config config)
    {
        config = new Config { BackupFolder = Path.Combine(root, "store"), SettingsDirectory = root, Preview = false, Thorough = true };
        string source = Rooted(Path.Combine(root, "source")); Put(Path.Combine(source, "song.txt"), "AAAA");
        string history = Path.Combine(HistoriesRoot(config), "profiles", "m8-test");
        var context = new OperationContext(new TestInteraction(), CancellationToken.None);
        using (context.Activate())
        {
            new BackupEngine().Run(source, history, null, false, config, context);
            string first = LatestBackup(history);
            Put(Path.Combine(source, "song.txt"), "BBBBBB");
            new BackupEngine().Run(source, history, first, true, config, context);
            new BackupEngine().Run(source, history, first, false, config, context);
            Put(Path.Combine(history, "2026-10-01.zip"), "ZIP");
            Put(Path.Combine(root, "mirrors", "x100vi-test", "photo.raf"), "PHOTO");
            return ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
        }
    }
    [Test("Storage management and dashboard records", Folder = "management")]
    static void TestBackupManagement(string root)
    {
        Config config; BackupCatalog catalog = ManagementFixture(root, out config);
        Assert(catalog.Warnings.Count == 0 && catalog.BackupList.Count == 2, "catalog discovers dated backups without warnings");
        Assert(catalog.BackupList.TrueForAll(b => b.Device == Devices.M8), "the catalog records each backup's device for the Dashboard filter");
        string damagedRecord = Path.Combine(root, "other", "profiles", "op-xy-ABC", "2026-10-01");
        Put(Path.Combine(AppIdentity.MetadataRoot(damagedRecord), "device.txt"), new string('x', 200));
        Assert(CatalogDevice(damagedRecord) == null, "a damaged device record does not infer identity from the history name");
        CatalogBackup old = catalog.BackupList.Find(b => !b.Latest), latest = catalog.BackupList.Find(b => b.Latest);
        Assert(old.FilesBytes == 0 && old.ObjectsBytes == 10, "storage separates plain files from the stored content objects");
        Assert(catalog.ZipBytes == 3 && catalog.MirrorBytes == 5 && catalog.UniqueBytes > 0, "storage includes ZIP archives and local MTP mirrors");
        Assert(latest.CompletedUtc != null && latest.CheckResult == "Never checked", "dashboard distinguishes successful backups from unperformed checks");
        Assert(!catalog.Cleanup.Exists(i => i.Kind == StorageKind.OlderBackup && i.Path == latest.Path), "latest backup is excluded from cleanup");
        Assert(!catalog.Cleanup.Exists(i => i.Path.IndexOf("\\objects\\", StringComparison.OrdinalIgnoreCase) >= 0), "history objects cannot be removed individually");
        foreach (StorageItem item in catalog.Cleanup)
        {
            long bytes;
            Assert(StorageStamp(item.Path, item.Folder, out bytes) == item.Stamp && bytes == item.Bytes,
                "a stamp built from the scan's own listing matches a fresh one: " + item.Kind);
        }
        StorageItem older = catalog.Cleanup.Find(i => i.Kind == StorageKind.OlderBackup);
        string added = Path.Combine(older.Path, "Added folder");
        long unused;
        Directory.CreateDirectory(added);
        Assert(StorageStamp(older.Path, true, out unused) != older.Stamp, "an added empty folder changes a backup's stamp");
        Directory.Delete(added);
        Assert(StorageStamp(older.Path, true, out unused) == older.Stamp, "removing it again restores the stamp");
        new IntegrityEngine().Run(new List<string> { latest.Path }, new OperationContext(new TestInteraction(), CancellationToken.None));
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors")); latest = catalog.BackupList.Find(b => b.Latest);
        Assert(latest.CheckedUtc != null && latest.CheckResult == "OK", "completed checks persist their time and result");
        Put(Path.Combine(AppIdentity.MetadataRoot(latest.Path), "objects", ContentStore.Current(latest.Path).Files.Find(f => f.Relative == "song.txt").Hash), "DAMAGE");
        new IntegrityEngine().Run(new List<string> { latest.Path }, new OperationContext(new TestInteraction(), CancellationToken.None));
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
        Assert(catalog.BackupList.Find(b => b.Latest).CheckResult == "Problems found", "dashboard records failed integrity checks");
        DateTime success = catalog.BackupList.Find(b => b.Latest).CompletedUtc.Value;
        WithUi(() => SetBackupStatus(latest.Path, Status.Failed));
        Assert(ScanBackupCatalog(config, Path.Combine(root, "mirrors")).BackupList.Find(b => b.Latest).CompletedUtc == success, "failed incremental runs preserve the last successful backup time");
        StorageItem busy = catalog.Cleanup.Find(i => i.Kind == StorageKind.OlderBackup); int recycled = 0;
        WithUi(() => {
            using (LockHistory(busy.History)) Throws(() => CleanupStorage(catalog, new List<StorageItem> { busy }, i => recycled++), "cleanup refuses a busy history");
            Put(Path.Combine(busy.Path, "added-after-scan.txt"), "CHANGED AFTER PREVIEW");
            Throws(() => CleanupStorage(catalog, new List<StorageItem> { busy }, i => recycled++), "cleanup refuses items changed after the scan");
        });
        Assert(recycled == 0, "validation failures recycle nothing");
        File.Delete(Path.Combine(busy.Path, "added-after-scan.txt"));
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
        StorageItem olderFolder = catalog.Cleanup.Find(i => i.Kind == StorageKind.OlderBackup);
        int folders = 0;
        WithUi(() => CleanupStorage(catalog, new List<StorageItem> { olderFolder, olderFolder }, i => folders++));
        Assert(folders == 1, "duplicate cleanup selections recycle an item only once");
        Throws(() => CleanupStorage(catalog, new List<StorageItem> { new StorageItem { Path = root } }, i => recycled++), "cleanup refuses targets that were not scanned");
        string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        string link = Path.Combine(latest.Path, "linked"); CreateTestJunction(link, outside);
        try {
            BackupCatalog linked = ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
            Assert(linked.Warnings.Count > 0 && !linked.Cleanup.Exists(i => Within(i.Path, latest.Path)), "linked content is reported and never offered for cleanup");
        } finally { Directory.Delete(link); }
        string linkedHistory = Path.Combine(HistoriesRoot(config), "profiles", "linked-profile"); CreateTestJunction(linkedHistory, outside);
        try { Assert(ScanBackupCatalog(config, Path.Combine(root, "mirrors")).Warnings.Count > 0, "linked profile roots are refused"); }
        finally { Directory.Delete(linkedHistory); }
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true }.Activate())
            Throws(() => CleanupStorage(catalog, new List<StorageItem> { olderFolder }, i => recycled++), "preview-only mode cannot execute cleanup");
    }
    // Detection, preferences, history and the run itself are shared by the CLI and GUI.
    [Test("Shared source flow", Folder = "shared-flow")]
    static void TestSharedFlow(string root)
    {
        string card = Rooted(Path.Combine(root, "tracker-card"));
        Put(Path.Combine(card, "trackerConfig"), "EEPROM");
        Put(Path.Combine(card, "Projects", "User", "Jam", "project.mt"), "JAM");
        var global = new Config { BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root, Preview = false };
        WithUi(() => SaveProfileSettings(global, "device:tracker-mini", new Dictionary<string, string> { { "BackupFolder", Path.Combine(root, "tracker-store") } }));
        PreparedSource prepared = null;
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            prepared = PrepareSource(global, new SourceChoice { Path = card });
            Assert(prepared.Device == Devices.TrackerMini && OperationContext.Current.Device == Devices.TrackerMini && prepared.Path == card,
                "preparing a source recognises its device and makes it current");
            Assert(prepared.Config.BackupFolder == Path.Combine(root, "tracker-store") && prepared.History.StartsWith(Path.Combine(root, "tracker-store")) &&
                Path.GetFileName(prepared.History).StartsWith("tracker-mini-folder-"), "the device's preferences choose where its history goes");
            Assert(RunPrepared(prepared, false, false).ExitCode == 0 && ListBackups(prepared.History).Count == 1, "a prepared source backs up");
            Throws(() => PrepareSource(global, new SourceChoice { Path = Path.Combine(root, "missing") }), "a missing source folder is refused");
            Throws(() => RunPrepared(new PreparedSource { Source = new SourceChoice { Mtp = new MtpSource { Device = Devices.X100VI } } }, true, false),
                "restoring onto an MTP device is refused before anything runs");
        }

        // Restore through the same steps: pick the history and backup, current files, all files.
        string target = Rooted(Path.Combine(root, "restore-target"));
        Directory.CreateDirectory(target);
        var ui = new TestInteraction();
        ui.OnChooseItem = (question, items) => question.StartsWith("Restore from which saved point") ? 0 : 1;
        ui.Keys.Enqueue(RestoreMode.All.Key);
        using (new OperationContext(ui, CancellationToken.None).Activate())
        {
            PreparedSource restoreTo = PrepareSource(global, new SourceChoice { Path = target, Detected = Devices.TrackerMini });
            restoreTo.History = prepared.History; restoreTo.Histories = prepared.Histories;
            OperationResult restored = RunPrepared(restoreTo, true, false);
            Assert(restored.ExitCode == 0 && File.ReadAllText(Path.Combine(target, @"Projects\User\Jam\project.mt")) == "JAM", "a prepared target restores");
        }

        // Settings backups never share a named profile's photo history.
        Assert(SettingsProfileName("Studio") == "Studio-settings" && SettingsProfileName("Studio-settings") == "Studio-settings",
            "a named profile's settings go to <profile>-settings");
        string longName = SettingsProfileName(new string('a', 64));
        Assert(longName.Length == 64 && longName.EndsWith("-settings"), "long profile names are shortened to fit");
        ValidateProfile(longName);
    }

    [Test("Readable X100VI recipes", Folder = "recipe-reading")]
    static void TestRecipeReading(string root)
    {
        Dictionary<string, byte[]> files;
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate()) files = CaptureFujiSettings(new FakeFujiSettings());
        string path = Path.Combine(root, "C1.xml"); Directory.CreateDirectory(root); File.WriteAllBytes(path, files["C1.xml"]);
        FujiRecipe recipe = ReadFujiRecipe(path);
        Assert(recipe.Name == "Recipe & <1>" && recipe.Values.Count == 23 && recipe.Values[4].Display == "PROVIA / Standard", "viewer reads names and all recipe settings");
        Assert(recipe.Values[12].Display == "-2" && recipe.Values[12].Raw == "FEFF", "viewer preserves signed shifts and raw bytes");
        Assert(FormatFujiRecipeValue(4, 20) == "REALA ACE" && FormatFujiRecipeValue(15, -15) == "-1.5" && FormatFujiRecipeValue(19, 8192) == "0", "simulation, tone scaling and non-linear noise reduction are readable");
        Assert(FormatFujiRecipeValue(7, 5) == "Strong · Large" && FormatFujiRecipeValue(11, 32775) == "Color temperature", "grain and white balance are decoded");
        Assert(FormatFujiRecipeValue(4, 600) == "Protocol value 600" && FormatFujiRecipeValue(15, -32768) == "Default / unavailable", "unknown and sentinel values are explicit");
        Assert(FujiRecipeReport(recipe).Contains("Film simulation: PROVIA / Standard"), "text export is readable");
        string original = Encoding.UTF8.GetString(files["C1.xml"]);
        Put(path, original.Replace("raw=\"FEFF\"", "raw=\"0000\"")); Throws(() => ReadFujiRecipe(path), "inconsistent values and raw bytes are refused");
        Put(path, original.Replace("0xD192", "0xD193")); Throws(() => ReadFujiRecipe(path), "mismatched codes are refused");
        Put(path, original.Replace("</GearBackupCameraSettings>", "<Setting name=\"Film simulation\" code=\"0xD192\" type=\"uint16\" raw=\"0100\">1</Setting></GearBackupCameraSettings>"));
        Throws(() => ReadFujiRecipe(path), "duplicate settings are refused");
        Put(path, original.Replace("<Section>C1</Section>", "<Section>C9</Section>")); Throws(() => ReadFujiRecipe(path), "unknown recipe slots are refused");
        Put(path, original.Replace("<Model>X100VI</Model>", "<Model>X100V</Model>")); Throws(() => ReadFujiRecipe(path), "other camera formats are refused");
        Put(path, "<!DOCTYPE x [<!ENTITY ex SYSTEM 'file:///private'>]>" + original.Substring(original.IndexOf("<GearBackupCameraSettings", StringComparison.Ordinal)));
        Throws(() => ReadFujiRecipe(path), "DTDs and external entities are refused");
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate()) files = CaptureFujiSettings(new FakeFujiSettings { MissingLive = true });
        File.WriteAllBytes(path, files["Shooting settings.xml"]);
        Assert(ReadFujiRecipe(path).Values.Exists(v => v.Display == "Unavailable"), "unavailable shooting settings remain explicit");
    }
}
