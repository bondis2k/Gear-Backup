using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

partial class GearBackup
{
    static void TestBackupDestination(string root)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Backup");
        Assert(new Config().BackupFolder == expected, "new configurations use the user profile Backup folder");
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

    static void TestProfilePreferences(string root)
    {
        var global = new Config { BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root, PointInTimeHistory = true };
        WithUi(() => {
            SaveProfileSettings(global, "device:m8", new Dictionary<string, string> { { "BackupFolder", Path.Combine(root, "device-store") }, { "PointInTimeHistory", "no" }, { "KeepBackups", "3" } });
            SaveProfileSettings(global, "profile:Studio", new Dictionary<string, string> { { "KeepBackups", "5" }, { "Verify", "yes" } });
        });
        Config resolved = ResolveProfileSettings(global, "m8", "Studio");
        Assert(!resolved.PointInTimeHistory && resolved.Verify && resolved.KeepBackups == 5 && resolved.BackupFolder == Path.Combine(root, "device-store"), "profile preferences override device defaults while inheriting other preferences");
        Assert(global.PointInTimeHistory && !global.Verify && global.KeepBackups == 0, "resolving preferences never mutates global settings");
        Config other = ResolveProfileSettings(global, "x100vi", "Other");
        Assert(other.PointInTimeHistory && !other.Verify && other.BackupFolder == global.BackupFolder, "unrelated devices and profiles keep global preferences");
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
            Assert(!resolved.PointInTimeHistory && HistoryFor(resolved, source).StartsWith(Path.Combine(root, "device-store")), "automatic profiles use device preferences before selecting their destination");
        }
        Assert(!Directory.Exists(global.BackupFolder) && !Directory.Exists(resolved.BackupFolder), "resolving preview preferences creates no backup folders");
    }
    static BackupCatalog ManagementFixture(string root, out Config config)
    {
        config = new Config { BackupFolder = Path.Combine(root, "store"), SettingsDirectory = root, Preview = false, Thorough = true, PointInTimeHistory = true };
        string source = Rooted(Path.Combine(root, "source")); Put(Path.Combine(source, "song.txt"), "AAAA");
        string history = Path.Combine(HistoriesRoot(config), "profiles", "m8-test");
        var context = new OperationContext(new TestInteraction(), CancellationToken.None);
        new BackupEngine().Run(source, history, null, false, config, context);
        string first = LatestBackup(history);
        Put(Path.Combine(source, "song.txt"), "BBBBBB");
        new BackupEngine().Run(source, history, first, true, config, context);
        new BackupEngine().Run(source, history, first, false, config, context);
        Put(Path.Combine(history, "2026-10-01.zip"), "ZIP");
        Put(Path.Combine(root, "mirrors", "x100vi-test", "photo.raf"), "PHOTO");
        return ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
    }
    // Several old versions recycled from one backup leave its checksum list in one rewrite, and a
    // cleanup that fails part-way still takes out what it already recycled.
    static void TestCleanupChecksums(string root)
    {
        var config = new Config { BackupFolder = Path.Combine(root, "store"), SettingsDirectory = root, Preview = false };
        string source = Rooted(Path.Combine(root, "source")), song = Path.Combine(source, "song.txt");
        string history = Path.Combine(HistoriesRoot(config), "profiles", "m8-test");
        var context = new OperationContext(new TestInteraction(), CancellationToken.None);
        Put(song, "A");
        new BackupEngine().Run(source, history, null, false, config, context);
        string backup = LatestBackup(history);
        foreach (string content in new[] { "BB", "CCC", "DDDD" })
        {
            Put(song, content);
            new BackupEngine().Run(source, history, backup, true, config, context);
        }
        BackupCatalog catalog = ScanBackupCatalog(config, Path.Combine(root, "no-mirrors"));
        List<StorageItem> versions = catalog.Cleanup.FindAll(i => i.Kind == StorageKind.OldVersion);
        Assert(versions.Count == 3, "three kept versions are offered for cleanup");
        int recycled = 0;
        WithUi(() => Throws(() => CleanupStorage(catalog, versions, item => {
            if (++recycled == 2) throw new IOException("Recycle Bin refused the file");
            File.Delete(item.Path);
        }), "a recycling failure stops the cleanup"));
        Dictionary<string, string> sums = LoadChecksums(backup);
        string first = versions[0].Path.Substring(Rooted(backup).Length), second = versions[1].Path.Substring(Rooted(backup).Length);
        Assert(!File.Exists(versions[0].Path) && !sums.ContainsKey(first), "a failed cleanup still removes what it recycled from the checksum list");
        Assert(sums.ContainsKey(second) && sums.ContainsKey("song.txt"), "entries for files still there are kept");

        catalog = ScanBackupCatalog(config, Path.Combine(root, "no-mirrors"));
        versions = catalog.Cleanup.FindAll(i => i.Kind == StorageKind.OldVersion);
        WithUi(() => CleanupStorage(catalog, versions, item => File.Delete(item.Path)));
        sums = LoadChecksums(backup);
        Assert(versions.Count == 2 && sums.Count == 1 && sums.ContainsKey("song.txt"), "recycling several versions of one backup updates its checksum list");
        int ok = 0, damaged = 0, missing = 0, uncheckedFiles = 0;
        WithUi(() => CheckBackup(backup, ref ok, ref damaged, ref missing, ref uncheckedFiles));
        Assert(ok == 1 && damaged == 0 && missing == 0, "the backup checks clean after cleanup");
    }

    static void TestBackupManagement(string root)
    {
        Config config; BackupCatalog catalog = ManagementFixture(root, out config);
        Assert(catalog.Warnings.Count == 0 && catalog.Backups.Count == 2, "catalog discovers dated backups without warnings");
        Assert(catalog.Backups.TrueForAll(b => b.Device == Devices.M8), "the catalog records each backup's device for the Dashboard filter");
        string damagedRecord = Path.Combine(root, "other", "profiles", "op-xy-ABC", "2026-10-01");
        Put(Path.Combine(AppIdentity.MetadataRoot(damagedRecord), "device.txt"), new string('x', 200));
        Assert(CatalogDevice(damagedRecord) == Devices.OPXY, "a damaged device record falls back to the history's name instead of hiding the backup");
        CatalogBackup old = catalog.Backups.Find(b => !b.Latest), latest = catalog.Backups.Find(b => b.Latest);
        Assert(old.FilesBytes == 6 && old.VersionsBytes == 4 && old.ObjectsBytes == 10, "storage separates current files, old versions and snapshot contents");
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
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors")); latest = catalog.Backups.Find(b => b.Latest);
        Assert(latest.CheckedUtc != null && latest.CheckResult == "OK", "completed checks persist their time and result");
        Put(Path.Combine(latest.Path, "song.txt"), "DAMAGE");
        new IntegrityEngine().Run(new List<string> { latest.Path }, new OperationContext(new TestInteraction(), CancellationToken.None));
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
        Assert(catalog.Backups.Find(b => b.Latest).CheckResult == "Problems found", "dashboard records failed integrity checks");
        DateTime success = catalog.Backups.Find(b => b.Latest).CompletedUtc.Value;
        WithUi(() => SetBackupStatus(latest.Path, Status.Failed));
        Assert(ScanBackupCatalog(config, Path.Combine(root, "mirrors")).Backups.Find(b => b.Latest).CompletedUtc == success, "failed incremental runs preserve the last successful backup time");
        StorageItem version = catalog.Cleanup.Find(i => i.Kind == StorageKind.OldVersion); int recycled = 0;
        WithUi(() => {
            using (LockHistory(version.History)) Throws(() => CleanupStorage(catalog, new List<StorageItem> { version }, i => recycled++), "cleanup refuses a busy history");
            Put(version.Path, "CHANGED AFTER PREVIEW");
            Throws(() => CleanupStorage(catalog, new List<StorageItem> { version }, i => recycled++), "cleanup refuses items changed after the scan");
        });
        Assert(recycled == 0, "validation failures recycle nothing");
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors")); version = catalog.Cleanup.Find(i => i.Kind == StorageKind.OldVersion);
        WithUi(() => CleanupStorage(catalog, new List<StorageItem> { version }, i => File.Delete(i.Path)));
        Assert(!File.Exists(version.Path) && !LoadChecksums(version.Backup).ContainsKey(Path.GetFileName(version.Path)), "old-version cleanup removes obsolete checksum entries");
        int ok = 0, damaged = 0, missing = 0, uncheckedFiles = 0;
        WithUi(() => { CheckBackup(old.Path, ref ok, ref damaged, ref missing, ref uncheckedFiles); CheckSnapshots(old.Path, ref ok, ref damaged, ref missing); });
        Assert(damaged == 0 && missing == 0, "old-version cleanup preserves snapshot contents and backup integrity");
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
        string safeSource = Rooted(Path.Combine(root, "dated-source"));
        Put(Path.Combine(safeSource, "legitimate_2026-10-01_120000.txt"), "USER FILE");
        Put(Path.Combine(safeSource, "plain.txt"), "OLD");
        string safeHistory = Path.Combine(HistoriesRoot(config), "profiles", "dated-source");
        var noHistory = config.Clone(); noHistory.PointInTimeHistory = false;
        new BackupEngine().Run(safeSource, safeHistory, null, false, noHistory, new OperationContext(new TestInteraction(), CancellationToken.None));
        string safeBackup = LatestBackup(safeHistory);
        Put(Path.Combine(safeSource, "plain.txt"), "NEW CONTENT");
        new BackupEngine().Run(safeSource, safeHistory, safeBackup, true, noHistory, new OperationContext(new TestInteraction(), CancellationToken.None));
        BackupCatalog safeCatalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
        Assert(!safeCatalog.Cleanup.Exists(i => i.Path == Path.Combine(safeBackup, "legitimate_2026-10-01_120000.txt")) &&
            safeCatalog.Cleanup.Exists(i => i.Kind == StorageKind.OldVersion && i.Backup == safeBackup), "ordinary dated source filenames are protected while history-free retained versions can be cleaned up");
        StorageItem safeVersion = safeCatalog.Cleanup.Find(i => i.Kind == StorageKind.OldVersion && i.Backup == safeBackup);
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true }.Activate())
            Throws(() => CleanupStorage(safeCatalog, new List<StorageItem> { safeVersion }, i => recycled++), "preview-only mode cannot execute cleanup");
        if (SupportsHardLinks(root))
        {
            string history = Path.Combine(HistoriesRoot(config), "profiles", "hardlink-test"); config.HardLinks = true;
            new BackupEngine().Run(Rooted(Path.Combine(root, "source")), history, null, false, config, new OperationContext(new TestInteraction(), CancellationToken.None));
            CatalogBackup linked = ScanBackupCatalog(config, Path.Combine(root, "mirrors")).Backups.Find(b => b.History == history);
            Assert(linked.UniqueBytes < linked.TotalBytes, "hard-linked backup and history contents are counted once");
        }
    }
    // Detection, preferences, history and the run itself are shared by the CLI and GUI.
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
        ui.Keys.Enqueue(RestoreMode.All);
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

    // The Dashboard/Storage scan's shortcuts give the same answers as the slow ways.
    static void TestCatalogShortcuts(string root)
    {
        Config config;
        BackupCatalog withRecord = ManagementFixture(root, out config);
        CatalogBackup old = withRecord.Backups.Find(b => !b.Latest);
        WithUi(() => {
            // Manifest names read without disk checks match the full reader's.
            foreach (string manifest in Directory.GetFiles(SnapshotRoot(old.Path), "*.manifest"))
            {
                var full = ReadSnapshot(old.Path, manifest).Files.ConvertAll(f => f.Relative);
                Assert(string.Join("|", SnapshotFileNames(manifest).ToArray()) == string.Join("|", full.ToArray()), "manifest names match the full reader");
            }
            string bad = Path.Combine(root, "bad.manifest");
            Put(bad, AppIdentity.SnapshotHeader + "\nF\tonly-two");
            Throws(() => SnapshotFileNames(bad), "a damaged manifest is still refused");

            // Without the old-version record (older backups), snapshots identify the same old versions.
            HashSet<string> recorded = KnownOldVersions(old.Path);
            File.Delete(Path.Combine(AppIdentity.MetadataRoot(old.Path), "old-versions.txt"));
            HashSet<string> fromSnapshots = KnownOldVersions(old.Path);
            Assert(recorded.Count == 1 && fromSnapshots.SetEquals(recorded), "old versions are found from snapshots without the record");
        });
        CatalogBackup rescanned = ScanBackupCatalog(config, Path.Combine(root, "mirrors")).Backups.Find(b => !b.Latest);
        Assert(rescanned.VersionsBytes == old.VersionsBytes && rescanned.FilesBytes == old.FilesBytes, "the scan reports the same sizes without the record");

        // Hard links share one identity; separate files don't, even with the same content.
        string folder = Folder(root, "identities");
        string first = Path.Combine(folder, "a.wav"), linked = Path.Combine(folder, "b.wav"), copy = Path.Combine(folder, "c.wav");
        Put(first, "SAME");
        Put(copy, "SAME");
        if (SupportsHardLinks(folder) && TryHardLink(first, linked))
        {
            var files = new List<FileInfo> { new FileInfo(first), new FileInfo(linked), new FileInfo(copy) };
            Dictionary<string, string> ids = null;
            WithUi(() => ids = FileIdentities(folder, files));
            Assert(ids[first] == ids[linked] && ids[first] != ids[copy], "hard links share an identity; copies don't");
            Assert(ids[first] == SharedFileId(first), "a folder's file IDs match the ones read file by file");
            Assert(FolderFileIds(Path.Combine(folder, "missing")) == null, "a folder that can't be listed falls back");
        }
    }

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
