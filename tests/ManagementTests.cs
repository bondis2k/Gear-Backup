using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

partial class GearBackup
{
    static void TestProfilePreferences(string root)
    {
        var global = new Config { BackupFolder = Path.Combine(root, "global"), SettingsDirectory = root };
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
        config = new Config { BackupFolder = Path.Combine(root, "store"), SettingsDirectory = root, Preview = false, Thorough = true };
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
    static void TestBackupManagement(string root)
    {
        Config config; BackupCatalog catalog = ManagementFixture(root, out config);
        Assert(catalog.Warnings.Count == 0 && catalog.Backups.Count == 2, "catalog discovers dated backups without warnings");
        CatalogBackup old = catalog.Backups.Find(b => !b.Latest), latest = catalog.Backups.Find(b => b.Latest);
        Assert(old.FilesBytes == 6 && old.VersionsBytes == 4 && old.ObjectsBytes == 10, "storage separates current files, old versions and snapshot contents");
        Assert(catalog.ZipBytes == 3 && catalog.MirrorBytes == 5 && catalog.UniqueBytes > 0, "storage includes ZIP archives and local MTP mirrors");
        Assert(latest.CompletedUtc != null && latest.CheckResult == "Never checked", "dashboard distinguishes successful backups from unperformed checks");
        Assert(!catalog.Cleanup.Exists(i => i.Kind == "Older backup" && i.Path == latest.Path), "latest backup is excluded from cleanup");
        Assert(!catalog.Cleanup.Exists(i => i.Path.IndexOf("\\objects\\", StringComparison.OrdinalIgnoreCase) >= 0), "history objects cannot be removed individually");
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
        StorageItem version = catalog.Cleanup.Find(i => i.Kind == "Old version"); int recycled = 0;
        WithUi(() => {
            using (LockHistory(version.History)) Throws(() => CleanupStorage(catalog, new List<StorageItem> { version }, i => recycled++), "cleanup refuses a busy history");
            Put(version.Path, "CHANGED AFTER PREVIEW");
            Throws(() => CleanupStorage(catalog, new List<StorageItem> { version }, i => recycled++), "cleanup refuses items changed after the scan");
        });
        Assert(recycled == 0, "validation failures recycle nothing");
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors")); version = catalog.Cleanup.Find(i => i.Kind == "Old version");
        WithUi(() => CleanupStorage(catalog, new List<StorageItem> { version }, i => File.Delete(i.Path)));
        Assert(!File.Exists(version.Path) && !LoadChecksums(version.Backup).ContainsKey(Path.GetFileName(version.Path)), "old-version cleanup removes obsolete checksum entries");
        int ok = 0, damaged = 0, missing = 0, uncheckedFiles = 0;
        WithUi(() => { CheckBackup(old.Path, ref ok, ref damaged, ref missing, ref uncheckedFiles); CheckSnapshots(old.Path, ref ok, ref damaged, ref missing); });
        Assert(damaged == 0 && missing == 0, "old-version cleanup preserves snapshot contents and backup integrity");
        catalog = ScanBackupCatalog(config, Path.Combine(root, "mirrors"));
        StorageItem olderFolder = catalog.Cleanup.Find(i => i.Kind == "Older backup");
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
            safeCatalog.Cleanup.Exists(i => i.Kind == "Old version" && i.Backup == safeBackup), "ordinary dated source filenames are protected while history-free retained versions can be cleaned up");
        StorageItem safeVersion = safeCatalog.Cleanup.Find(i => i.Kind == "Old version" && i.Backup == safeBackup);
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
