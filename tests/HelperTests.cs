// Focused tests for the helpers the backup, restore and check workflows depend on: change summaries,
// name warnings, file classification, comparisons, path safety, checksum lists, settings, retention,
// the resume question and the zip's failure paths. Each runs as a named test; see RunTests in TestRunner.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

partial class GearBackup
{
    static void TestHelpers(string root)
    {
        string dir = Path.Combine(root, "helpers");
        Directory.CreateDirectory(dir);
        RunTest("Helpers: file classification", TestClassification);
        RunTest("Helpers: comparisons", () => TestComparisons(dir));
        RunTest("Helpers: path safety", () => TestPathSafety(dir));
        RunTest("Helpers: change summary and name warnings", () => TestChangesAndNames(dir));
        RunTest("Helpers: checksum lists", () => TestChecksumLists(dir));
        RunTest("Helpers: settings", () => TestSettings(dir));
        RunTest("Helpers: retention", () => TestRetention(dir));
        RunTest("Helpers: resume question", () => TestResumeQuestion(dir));
        RunTest("Helpers: zip failures", () => TestZipFailures(dir));
        RunTest("Security hardening", () => TestSecurity(dir));
        RunTest("Security: MTP mirror links", () => TestMirrorLinks(dir));
        RunTest("Security: additional mirror link types", () => TestAdditionalMirrorLinks(dir));
        RunTest("Security: file symbolic links in mirrors", () => TestMirrorFileSymlinks(dir));
        RunTest("MTP disconnection preserves mirror", () => TestMtpDisconnect(dir));
        RunTest("Exact restore preserves extras after failure", () => TestPartialExactRestore(dir));
        RunTest("Malformed snapshot manifests", () => TestMalformedSnapshots(dir));
        RunTest("Security: atomic text temporary files", () => TestAtomicText(dir));
        RunTest("Console questions", TestConsoleQuestions);
        RunTest("Device profiles", () => TestDevices(dir));
        RunTest("OP-1 Field: MTP mirror", () => TestMtpMirror(dir));
        RunTest("OP-1 Field: backup of a mirrored device", () => TestOp1FieldBackup(dir));
        RunTest("OP-XY: projects and backup", () => TestOpXy(dir));
        RunTest("Tracker Mini: project folders", () => TestTrackerMini(dir));
        RunTest("MTP devices through the shared flow", () => TestMtpSharedFlow(Folder(dir, "mtp-flow")));
        RunTest("History names per device", () => TestProfileNames(dir));
        RunTest("X100VI: photos", () => TestX100VI(dir));
        RunTest("X100VI: settings capture and selector safety", () => TestFujiSettings(Folder(dir, "fuji-settings")));
        RunTest("Gear Backup identity and upgrade compatibility", () => TestIdentityUpgrade(Folder(root, "identity-upgrade")));
    }

    static void TestIdentityUpgrade(string root)
    {
        string settings = Folder(root, "settings");
        string oldIni = Path.Combine(settings, AppIdentity.Legacy.ConfigFileName);
        string newIni = Path.Combine(settings, AppIdentity.ConfigFileName);
        Put(oldIni, "; " + AppIdentity.Legacy.Name + " settings\n; keep this comment\nProfile=Studio M8\nVerify=yes\nCustomKey=preserved\n");
        Config loaded = null;
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true }.Activate())
            loaded = LoadConfig(settings);
        Assert(loaded.Profile == "Studio M8" && loaded.Verify && !File.Exists(newIni),
            "preview reads old settings without writing a renamed file");
        WithUi(() => loaded = LoadConfig(settings));
        string migrated = File.ReadAllText(newIni);
        Assert(loaded.Profile == "Studio M8" && loaded.Verify && !loaded.Thorough && !loaded.Recovery,
            "settings migration preserves preferences and default-off options");
        Assert(migrated.Contains("; Gear Backup settings") && migrated.Contains("; keep this comment") &&
            migrated.Contains("CustomKey=preserved") && File.Exists(oldIni), "migration preserves custom settings and the original INI");
        Put(newIni, "Profile=New settings\nVerify=no\n");
        WithUi(() => loaded = LoadConfig(settings));
        Assert(loaded.Profile == "New settings" && !loaded.Verify, "renamed settings take precedence over the old INI");

        Fixture f = BackedUpCard(Folder(root, "fixture"), false);
        string oldRoot = Path.Combine(root, AppIdentity.Legacy.BackupsFolderName);
        string history = Path.Combine(oldRoot, "profiles", "studio");
        Directory.CreateDirectory(Path.GetDirectoryName(history));
        // Windows scanners can briefly hold freshly written fixture files open. Allow a bounded
        // retry for access/sharing errors; persistent locks still fail the compatibility test.
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(f.History, history); break; }
            catch (IOException ex)
            {
                int error = ex.HResult & 0xffff;
                if (attempt >= 20 || (error != 5 && error != 32)) throw;
                Thread.Sleep(100);
            }
        }
        string backup = ListBackups(history)[0];
        Directory.Move(Path.Combine(backup, AppIdentity.MetadataFolderName), Path.Combine(backup, AppIdentity.Legacy.MetadataFolderName));
        foreach (string manifest in Directory.GetFiles(SnapshotRoot(backup), "*.manifest"))
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(AppIdentity.SnapshotHeader, AppIdentity.Legacy.SnapshotHeader));
        var config = new Config { BackupFolder = root, Profile = "studio", Preview = false, Thorough = true, Verify = true };
        Assert(HistoriesRoot(config) == oldRoot && HistoryFor(config, f.Source) == history, "an existing history is reused in place");
        Assert(BackupStatus(backup) == Status.Complete && LatestSnapshot(backup).Files.Count == f.First.Files.Count,
            "old status files, manifests and objects remain readable");
        Assert(CurrentBackup(backup).Files.Count == f.First.Files.Count, "old metadata is excluded from browsable content");
        OperationResult updated = null;
        WithUi(() => updated = BackupWithLock(f.Source, history, true, config));
        Assert(updated.ExitCode == 0 && Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == 2,
            "incremental backup continues the old history and publishes a new snapshot");
        string target = Rooted(Folder(root, "restored"));
        Snapshot snapshot = LatestSnapshot(backup);
        OperationResult restored = new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, 'a', null, false,
            config, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(restored.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "AAAA",
            "snapshots from an existing history restore correctly");
        Assert(new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(new TestInteraction(), CancellationToken.None)).ExitCode == 0,
            "integrity checks work across old and new snapshot headers");
        string zipPath = null;
        WithUi(() => zipPath = ZipBackup(backup));
        using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(zipPath), System.IO.Compression.ZipArchiveMode.Read))
            Assert(!new List<System.IO.Compression.ZipArchiveEntry>(zip.Entries).Exists(e =>
                e.FullName.StartsWith(AppIdentity.Legacy.MetadataFolderName) || e.FullName.StartsWith(AppIdentity.MetadataFolderName)),
                "ZIP exports exclude both metadata directory names");
        string fresh = Folder(root, "fresh");
        Assert(HistoriesRoot(new Config { BackupFolder = fresh }) == Path.Combine(fresh, AppIdentity.BackupsFolderName),
            "fresh installations use the new backup directory");
        string localData = Folder(root, "local-data");
        Directory.CreateDirectory(Path.Combine(localData, AppIdentity.Legacy.DirectoryName, "Devices"));
        Assert(AppIdentity.ExistingFolder(localData, AppIdentity.DirectoryName, AppIdentity.Legacy.DirectoryName) ==
            Path.Combine(localData, AppIdentity.Legacy.DirectoryName), "existing MTP mirror folders are reused");
    }

    // Runs action against the real console front end with scripted input; returns what it printed.
    static string OnConsole(string input, Action<ConsoleInteraction> action)
    {
        TextWriter oldOut = Console.Out;
        TextReader oldIn = Console.In;
        var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetIn(new StringReader(input));
            action(new ConsoleInteraction());
        }
        finally { Console.SetOut(oldOut); Console.SetIn(oldIn); }
        return output.ToString();
    }

    static void TestConsoleQuestions()
    {
        char key = ' ';
        string shown = OnConsole("x\nk\n", c => key = c.Choose("Some target files differ from the backup.", new[] {
            new Option('r', "Replace them"), new Option('k', "Keep them (copy only missing files)"), new Option('c', "Cancel") }));
        Assert(key == 'k', "console accepts an option's key, ignoring other input");
        Assert(shown.Contains("Some target files differ from the backup. [R]eplace them, [K]eep them (copy only missing files) or [C]ancel?"),
            "console shows options inline, with each key's letter bracketed");

        shown = OnConsole("d\n", c => key = c.Choose("What should be restored?", new[] {
            new Option('a', "All files"), new Option('d', "Song with dependent samples") }));
        Assert(key == 'd' && shown.Contains("[A]ll files or Song with [D]ependent samples?"), "the bracketed letter can be inside a word");

        shown = OnConsole("2\n", c => key = c.Choose("What would you like to do?", new[] {
            new Option('1', "Full backup"), new Option('2', "Restore") }));
        Assert(key == '2' && shown.Contains("    1) Full backup") && shown.Contains("What would you like to do? (1 or 2)"),
            "options whose key isn't in the label are shown as a numbered menu");

        shown = OnConsole("n\n", c => key = c.Choose("Move them to the Recycle Bin?", new[] { Yes, No }));
        Assert(key == 'n' && shown.Contains("Move them to the Recycle Bin? (y/n)"), "yes/no questions end in (y/n)");

        int item = -1;
        shown = OnConsole("9\n2\n", c => item = c.ChooseItem("Choose a bundle.", new[] { "Bundles\\a", "Bundles\\b" }, null));
        Assert(item == 2 && shown.Contains(" 1) Bundles\\a") && shown.Contains("Please enter a number from 1 to 2"),
            "console lists the items and asks again after an invalid number");
        Assert(shown.Contains("Number, or Enter to cancel"), "a list without a 'none' option offers Enter to cancel");
        shown = OnConsole("\n", c => item = c.ChooseItem("Choose a file.", new[] { "a.m8s" }, "Search again"));
        Assert(item == 0 && shown.Contains("Number, or Enter for search again"), "Enter picks the 'none' option");

        string text = null;
        OnConsole("jam\n", c => text = c.AskText("Search file name:"));
        Assert(text == "jam", "console reads free text");
    }

    // Runs action with a scripted front end and returns it, so tests can read its output and questions.
    static TestInteraction WithUi(Action action, params char[] keys)
    {
        var ui = new TestInteraction();
        foreach (char key in keys) ui.Keys.Enqueue(key);
        using (new OperationContext(ui, CancellationToken.None).Activate()) action();
        return ui;
    }

    static void TestClassification()
    {
        foreach (string name in new[] { ".DS_Store", "._Song.m8s", "notes.txt~", "FSCK0000.REC", "fsck0123.rec", "Thumbs.db", "desktop.ini", "ehthumbs.db" })
            Assert(IsLeftover(name, false), "leftover file recognised: " + name);
        foreach (string name in new[] { ".Trash-1000", ".Spotlight-V100", "lost+found" })
            Assert(IsLeftover(name, true), "leftover folder recognised: " + name);
        foreach (string name in new[] { "Song.m8s", "kick.wav", "FSCK.REC", "Thumbs.db.wav" })
            Assert(!IsLeftover(name, false), "ordinary file kept: " + name);
        Assert(!IsLeftover("Songs", true) && !IsLeftover("lost+found", false), "ordinary folder kept; lost+found only skipped as a folder");

        Assert(IsOldVersion(@"Songs\Jam_2026-10-01_143000.m8s") && IsOldVersion(@"Songs\Jam_2026-10-01_143000_2.m8s"),
            "dated old versions recognised, including _2 duplicates");
        Assert(!IsOldVersion(@"Songs\Jam.m8s") && !IsOldVersion(@"Songs\Jam_2026-10-01.m8s"), "names without a full date and time are not old versions");
        Assert(OriginalName(@"Songs\Jam_2026-10-01_143000_2.m8s") == @"Songs\Jam.m8s", "old version maps back to its original name");
        Assert(IsSong("a.M8S") && !IsSong("a.m8i"), "songs are .m8s files, case-insensitive");
        Assert(SongName(@"Songs\Live\Jam.m8s") == @"Live\Jam" && SongName(@"Bundles\b\x.m8s") == @"Bundles\b\x",
            "song display names drop the Songs folder and extension");
        Assert(IsStatisticsFile(@"x\backup-statistics_2026.txt") && !IsStatisticsFile(@"x\backup-statistics.log"), "statistics files recognised");
        Assert(IsBackupMetaFile(@"b\.gearbackup\objects\abc") && IsBackupMetaFile(@"b\checksums.sha256.tmp") && !IsBackupMetaFile(@"b\Songs\a.m8s"),
            "program metadata recognised; card files are not metadata");

        string hash = new string('a', 64);
        Assert(ValidHash(hash) && !ValidHash(hash.ToUpperInvariant()) && !ValidHash(hash.Substring(1)) && !ValidHash(null),
            "checksums must be 64 lower-case hex digits");
        Assert(ToHex(new byte[] { 0, 15, 255 }) == "000fff", "checksums are written as lower-case hex");
        Assert(FormatSize(0) == "0 B" && FormatSize(1023) == "1023 B" && FormatSize(5L << 30).EndsWith(" GB"), "sizes use B up to 1 KB, then units");
        Assert(FormatTime(TimeSpan.FromSeconds(125)) == "2m 5s", "times over a minute show minutes and seconds");
    }

    static void TestComparisons(string dir)
    {
        string a = Path.Combine(dir, "cmp-a.bin"), b = Path.Combine(dir, "cmp-b.bin");
        Put(a, "SAME"); Put(b, "SAME");
        DateTime time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, time);
        File.SetLastWriteTimeUtc(b, time.AddSeconds(1));
        Assert(Compare(new FileInfo(a), b) == FileState.Same, "times within FAT's 2-second steps count as the same");
        File.SetLastWriteTimeUtc(b, time.AddSeconds(3));
        Assert(Compare(new FileInfo(a), b) == FileState.Changed, "times more than 2 seconds apart count as changed");
        Put(b, "LONGER"); File.SetLastWriteTimeUtc(b, time);
        Assert(Compare(new FileInfo(a), b) == FileState.Changed, "a different size counts as changed");
        Assert(Compare(new FileInfo(a), Path.Combine(dir, "missing.bin")) == FileState.New, "a missing copy counts as new");
    }

    static void TestPathSafety(string dir)
    {
        string safeRoot = Path.Combine(dir, "safe");
        Directory.CreateDirectory(safeRoot);
        Assert(SafePath(safeRoot, @"Songs\Live\a.m8s") == Path.Combine(safeRoot, @"Songs\Live\a.m8s"), "nested relative paths are allowed");
        Assert(SafePath(safeRoot, "Songs/a.m8s") == Path.Combine(safeRoot, @"Songs\a.m8s"), "forward slashes are accepted");
        foreach (string bad in new[] { @"..\x", @"Songs\..\..\x", @"C:\x", @"\x", "a:stream", @"Songs\.", @"Songs\a.", @"Songs\a ", @"Songs\\a", "" })
            Throws(() => SafePath(safeRoot, bad), "unsafe relative path rejected: [" + bad + "]");
        Assert(!Within(@"C:\Music\Gear Backups2", @"C:\Music\Gear Backups") && Within(@"C:\Music\Gear Backups\x", @"C:\Music\Gear Backups"),
            "a folder with a longer name isn't inside a similarly named one");
        Assert(Rooted(@"C:\Music\") == Rooted(@"C:\Music") && Rooted(@"C:\Music").EndsWith(@"\"), "rooted paths end with exactly one backslash");
        Put(Path.Combine(safeRoot, "blocker"), "file");
        Throws(() => CheckParents(safeRoot, Path.Combine(safeRoot, @"blocker\a.m8s")), "a file where a folder must go is reported");
    }

    static void TestChangesAndNames(string dir)
    {
        string source = Rooted(Path.Combine(dir, "changes-card"));
        Put(Path.Combine(source, "Songs", "A.m8s"), "A");
        Put(Path.Combine(source, "Songs", "B.m8s"), "B");
        Put(Path.Combine(source, "Samples", "Café.wav"), "x");
        Put(Path.Combine(source, "Samples", new string('n', 70) + ".wav"), "x");
        Put(Path.Combine(source, "Samples", " padded.wav"), "x");
        string baseline = Path.Combine(dir, "changes-baseline");
        Put(Path.Combine(baseline, "Songs", "A.m8s"), "old A");
        Put(Path.Combine(baseline, "Songs", "Gone.m8s"), "gone");
        Put(Path.Combine(baseline, "Songs", "A_2026-01-01_120000.m8s"), "old version");

        var scan = new ScanResult();
        CollectFiles(source, scan);
        var states = new FileState[scan.Files.Count];
        for (int i = 0; i < states.Length; i++)
        {
            string name = scan.Files[i].Name;
            states[i] = name == "A.m8s" ? FileState.Changed : name == "B.m8s" ? FileState.New : FileState.Same;
        }
        Changes changes = FindChanges(scan, source, states, baseline);
        Assert(changes.NewSongs.Count == 1 && changes.NewSongs[0] == "B" && changes.EditedSongs.Count == 1 && changes.EditedSongs[0] == "A",
            "change summary finds new and edited songs");
        Assert(changes.RemovedSongs.Count == 1 && changes.RemovedSongs[0] == "Gone", "songs gone from the card are reported; old versions are not");
        Assert(ShortSongSummary(changes) == " Songs: 1 new (B), 1 edited (A).", "notification summary names the songs");
        Assert(ShortSongSummary(new Changes { HasBaseline = true }) == " No songs changed." && ShortSongSummary(new Changes()) == "",
            "summary says when nothing changed, and nothing on a first backup");
        Assert(FirstNames(new List<string> { "a", "b", "c", "d" }) == "a, b, c, ...", "long name lists are shortened");

        List<string> warnings = CheckNames(scan, source, 64);
        Assert(warnings.Exists(w => w.Contains("Café.wav")) && warnings.Exists(w => w.Contains("characters long")) &&
            warnings.Exists(w => w.Contains("space")), "name warnings cover special characters, long names and spaces");
        Assert(!warnings.Exists(w => w.Contains("A.m8s")), "ordinary names get no warning");
        Assert(!CheckNames(scan, source, 0).Exists(w => w.Contains("characters long")), "MaxNameLength=0 turns off the length check");
    }

    static void TestChecksumLists(string dir)
    {
        string backup = Path.Combine(dir, "sums-backup");
        Put(Path.Combine(backup, "Songs", "a.m8s"), "A");
        string good = HashFile(Path.Combine(backup, "Songs", "a.m8s"));
        Put(Path.Combine(backup, ChecksumFileName), good + "  Songs/a.m8s\nnot a checksum line\n" + good + " *Songs/b.m8s\n");
        Dictionary<string, string> sums = LoadChecksums(backup);
        Assert(sums.Count == 2 && sums[@"Songs\a.m8s"] == good && sums.ContainsKey(@"Songs\b.m8s"),
            "checksum lists accept both sha256sum line styles and skip lines that aren't checksums");
        Put(Path.Combine(backup, ChecksumFileName), good + "  ../outside.m8s\n");
        Throws(() => LoadChecksums(backup), "a checksum list can't point outside its backup");
        Put(Path.Combine(backup, ChecksumFileName), new string('Z', 64) + "  Songs/a.m8s\n");
        Throws(() => LoadChecksums(backup), "a malformed checksum is rejected");
        Assert(LoadChecksums(Path.Combine(dir, "no-such-backup")) == null, "a backup without a list has no checksums");

        // A check of a backup without a list offers to create one; afterwards it compares against it.
        File.Delete(Path.Combine(backup, ChecksumFileName));
        Put(Path.Combine(backup, "Samples", "kick.wav"), "KICK");
        int ok = 0, damaged = 0, missing = 0, unchecked_ = 0;
        WithUi(() => CheckBackup(backup, ref ok, ref damaged, ref missing, ref unchecked_), 'y');
        Assert(LoadChecksums(backup).Count == 2 && unchecked_ == 0, "checking a backup without a list can create one");
        Put(Path.Combine(backup, "Samples", "kick.wav"), "KICX");
        File.Delete(Path.Combine(backup, "Songs", "a.m8s"));
        Put(Path.Combine(backup, "extra.txt"), "added by hand");
        TestInteraction ui = WithUi(() => CheckBackup(backup, ref ok, ref damaged, ref missing, ref unchecked_));
        Assert(damaged == 1 && missing == 1, "a check reports damaged and missing files");
        Assert(ui.Output.ToString().Contains("1 file(s) in the folder aren't in the checksum list"), "files added by hand are mentioned, not checked");
        int ok2 = 0, damaged2 = 0, missing2 = 0, unchecked2 = 0;
        string bare = Path.Combine(dir, "sums-bare");
        Put(Path.Combine(bare, "a.m8s"), "A");
        WithUi(() => CheckBackup(bare, ref ok2, ref damaged2, ref missing2, ref unchecked2), 'n');
        Assert(unchecked2 == 1 && LoadChecksums(bare) == null, "declining to create a list counts the backup as unchecked");
    }

    static void TestSettings(string dir)
    {
        // Every saved setting survives a save and reload; Zip is per run and isn't saved.
        var saved = new Config { BackupFolder = Path.Combine(dir, "store"), Mode = BackupMode.Incremental, Verify = true, KeepBackups = 4,
            MaxNameLength = 40, Eject = EjectMode.Ask, KeepOldVersions = 3, Recovery = true, RecoveryFolder = Path.Combine(dir, "rec"),
            SourceFolder = Path.Combine(dir, "card"), Profile = "Studio", Thorough = true, Preview = false, HardLinks = true, PointInTimeHistory = false, Zip = true };
        string ini = Path.Combine(dir, "roundtrip.ini");
        WithUi(() => SaveConfig(saved, ini));
        var loaded = new Config();
        WithUi(() => {
            foreach (string line in File.ReadAllLines(ini))
            {
                int equals = line.IndexOf('=');
                if (equals > 0 && !line.StartsWith(";")) ApplySetting(loaded, line.Substring(0, equals), line.Substring(equals + 1));
            }
        });
        foreach (var field in typeof(Config).GetFields())
            if (field.Name != "Zip")
                Assert(Equals(field.GetValue(loaded), field.GetValue(saved)), "setting survives save and reload: " + field.Name);
        Assert(!loaded.Zip && !File.ReadAllText(ini).Contains("Zip="), "the zip choice isn't saved");

        // Invalid values warn and keep the default.
        var config = new Config();
        TestInteraction ui = WithUi(() => {
            ApplySetting(config, "Mode", "sometimes");
            ApplySetting(config, "KeepBackups", "-1");
            ApplySetting(config, "Eject", "maybe");
            ApplySetting(config, "Verify", "perhaps");
        });
        Assert(config.Mode == BackupMode.Ask && config.KeepBackups == 0 && config.Eject == EjectMode.No && !config.Verify, "invalid values keep the defaults");
        Assert(ui.Output.ToString().Contains("Mode=sometimes isn't valid") && ui.Output.ToString().Contains("KeepBackups=-1 isn't valid"),
            "invalid values are reported");
        WithUi(() => { ApplySetting(config, "VERIFY", "On"); ApplySetting(config, "eject", "YES"); ApplySetting(config, "Mode", "Full"); });
        Assert(config.Verify && config.Eject == EjectMode.Yes && config.Mode == BackupMode.Full, "setting names and values aren't case-sensitive");

        // The Settings table describes every saved Config field exactly once.
        var keys = new List<string>();
        foreach (Setting setting in Settings) keys.Add(setting.Key);
        foreach (var field in typeof(Config).GetFields())
            if (field.Name != "Zip") Assert(keys.FindAll(k => k == field.Name).Count == 1, "one setting describes " + field.Name);
        Assert(keys.Count == typeof(Config).GetFields().Length - 1, "every setting is a Config field");
        string fresh = Path.Combine(dir, "fresh-settings");
        Directory.CreateDirectory(fresh);
        WithUi(() => LoadConfig(fresh));
        string written = File.ReadAllText(Path.Combine(fresh, AppIdentity.ConfigFileName));
        foreach (Setting setting in Settings)
            Assert(written.Split(new[] { "\n" + setting.Key + "=" }, StringSplitOptions.None).Length == 2, "a new INI documents " + setting.Key + " once");
        Assert(written.StartsWith("; " + AppIdentity.Name + " settings") && written.Contains("\nMaxNameLength=64\r\n") && written.Contains("\nDevice=auto\r\n"),
            "a new INI starts with its title and lists defaults");
        var global = new Config { BackupFolder = dir, SettingsDirectory = dir };
        Throws(() => SaveProfileSettings(global, "device:m8", new Dictionary<string, string> { { "KeepBackups", "-1" } }), "invalid preference values aren't saved");
        Throws(() => SaveProfileSettings(global, "device:m8", new Dictionary<string, string> { { "Eject", "maybe" } }), "list preferences only take their values");
        Throws(() => SaveProfileSettings(global, "device:m8", new Dictionary<string, string> { { "Mode", "full" } }), "settings that aren't preferences can't be overridden");
        WithUi(() => SaveProfileSettings(global, "device:m8", new Dictionary<string, string> { { "verify", "On" }, { "Eject", "ASK" } }));
        Assert(File.ReadAllText(ProfileSettingsPath(global)).Contains("Verify=On") &&
            ResolveProfileSettings(global, "m8", "").Verify && ResolveProfileSettings(global, "m8", "").Eject == EjectMode.Ask,
            "preferences are saved under their proper key and read like the INI");
    }

    static void TestRetention(string dir)
    {
        // Backup folders: only date-named ones count, sorted oldest first.
        string history = Path.Combine(dir, "retention");
        foreach (string name in new[] { "2026-10-09_143000", "2026-10-01", "2026-10-09", "notes", "2026-13-99" })
            Directory.CreateDirectory(Path.Combine(history, name));
        Put(Path.Combine(history, "2026-10-10.zip"), "zip");
        List<string> backups = ListBackups(history);
        Assert(backups.Count == 3 && Path.GetFileName(backups[0]) == "2026-10-01" && Path.GetFileName(backups[2]) == "2026-10-09_143000",
            "backup folders are date-named folders, oldest first; other folders and files are ignored");
        Assert(ListBackups(Path.Combine(dir, "no-history")).Count == 0, "a missing history has no backups");

        // KeepBackups lists the oldest extras and asks; answering no keeps everything.
        TestInteraction ui = WithUi(() => PruneBackups(history, 2, backups[2]), 'n');
        var offered = new List<string>();
        foreach (string line in ui.Output.ToString().Split('\n'))
            if (line.StartsWith("    ")) offered.Add(line.Trim());
        Assert(offered.Count == 1 && offered[0] == "2026-10-01" && ui.Questions.Exists(q => q.Contains("Recycle Bin")),
            "KeepBackups offers only the oldest extra backup, never the current one");
        Assert(ListBackups(history).Count == 3, "answering no keeps every backup");
        ui = WithUi(() => PruneBackups(history, 5, backups[2]));
        Assert(ui.Questions.Count == 0, "no question when there are no extra backups");

        // KeepOldVersions groups old versions per file and offers the oldest ones.
        string backup = Path.Combine(dir, "versions");
        foreach (string name in new[] { "Jam_2026-01-01_120000", "Jam_2026-02-01_120000", "Jam_2026-03-01_120000", "Solo_2026-01-01_120000", "Jam" })
            Put(Path.Combine(backup, "Songs", name + ".m8s"), name);
        int moved = 0;
        ui = WithUi(() => { moved = PruneOldVersions(backup, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }, 'n');
        string listed = ui.Output.ToString();
        Assert(moved == 0 && listed.Contains("Jam_2026-01-01_120000") && listed.Contains("Jam_2026-02-01_120000") &&
            !listed.Contains("Jam_2026-03-01_120000") && !listed.Contains("Solo_"), "KeepOldVersions offers each file's oldest extra versions");
        ui = WithUi(() => PruneOldVersions(backup, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            @"Songs\Jam_2026-01-01_120000.m8s", @"Songs\Jam_2026-02-01_120000.m8s" }), 'n');
        Assert(ui.Questions.Count == 0, "dated files that are also on the card aren't treated as old versions");
        Assert(VersionedName(Path.Combine(backup, "Songs", "Jam.m8s"), new DateTime(2026, 1, 1, 12, 0, 0)).EndsWith("Jam_2026-01-01_120000_2.m8s"),
            "a taken old-version name gets _2");
    }

    static void TestResumeQuestion(string dir)
    {
        string backup = Path.Combine(dir, "resume-backup");
        Directory.CreateDirectory(backup);
        var config = new Config();
        bool? answer = true;
        TestInteraction ui = WithUi(() => { answer = ConfirmResume(backup, true, config); });
        Assert(answer == true && ui.Questions.Count == 0, "no question when the latest backup has no unfinished status");
        SetBackupStatus(backup, Status.Complete);
        WithUi(() => { answer = ConfirmResume(backup, false, config); });
        Assert(answer == false, "a complete backup keeps the chosen mode");
        SetBackupStatus(backup, Status.Incomplete);
        WithUi(() => { answer = ConfirmResume(backup, false, config); }, 'r');
        Assert(answer == true, "Resume continues into the unfinished backup");
        WithUi(() => { answer = ConfirmResume(backup, true, config); }, 'n');
        Assert(answer == false, "New full backup is chosen instead of resuming");
        SetBackupStatus(backup, Status.Failed);
        WithUi(() => { answer = ConfirmResume(backup, true, config); }, 'c');
        Assert(answer == null, "Cancel returns no choice");
        Assert(ConfirmResume(null, true, config) == true, "with no backup yet, nothing is asked");
        Assert(BackupStatus(Path.Combine(dir, "never-backed-up")) == Status.Legacy, "backups without a status file are legacy");

        var paths = new Config { BackupFolder = Path.Combine(dir, "card-inside") };
        Throws(() => HistoryFor(paths, Path.Combine(dir, "card-inside", "Gear Backups", "card")), "a source inside the backup location is rejected");
        Throws(() => HistoryFor(new Config { BackupFolder = Path.Combine(dir, "outer", "inner") }, Path.Combine(dir, "outer")),
            "a backup location inside the source is rejected");
    }

    static void TestZipFailures(string dir)
    {
        string source = Rooted(Path.Combine(dir, "zipfail-card"));
        Put(Path.Combine(source, "Songs", "a.m8s"), "A");
        string history = Path.Combine(dir, "zipfail-history");
        var config = new Config { BackupFolder = dir, Preview = false, Zip = true };

        // ZipPathFor never overwrites an existing zip.
        string folder = Path.Combine(history, "2026-10-10");
        Directory.CreateDirectory(folder);
        Put(folder + ".zip", "existing");
        Assert(ZipPathFor(folder).EndsWith("2026-10-10_2.zip") && ZipPathFor(folder + "\\").EndsWith("2026-10-10_2.zip"),
            "a new zip never overwrites an existing one");
        Directory.Delete(history, true);

        // A zip that can't be written: the backup still completes, the run reports exit code 2.
        string date = DateTime.Now.ToString("yyyy-MM-dd");
        Directory.CreateDirectory(Path.Combine(history, date + ".zip.tmp"));
        var ui = new TestInteraction();
        OperationResult result = new BackupEngine().Run(source, history, null, false, config, new OperationContext(ui, CancellationToken.None));
        string backup = ListBackups(history)[0];
        Assert(result.ExitCode == 2 && result.Summary.Contains("zip could not be saved") && BackupStatus(backup) == Status.Complete,
            "a zip that can't be saved gives exit code 2, but the backup stays complete");
        Directory.Delete(Path.Combine(history, date + ".zip.tmp"));

        // Cancelling while zipping leaves no partial zip; the finished backup stays complete.
        var cts = new CancellationTokenSource();
        ui = new TestInteraction();
        ui.OnProgress = delegate(OperationProgress p) { if (p.Stage == Stage.Zipping) cts.Cancel(); };
        Put(Path.Combine(source, "Samples", "b.wav"), "B");
        result = new BackupEngine().Run(source, history, null, false, config, new OperationContext(ui, cts.Token));
        string second = ListBackups(history)[1];
        Assert(result.Cancelled && BackupStatus(second) == Status.Complete, "cancelling the zip leaves the finished backup complete");
        Assert(Directory.GetFiles(history, "*.tmp").Length == 0 && !File.Exists(second + ".zip"), "a cancelled zip leaves nothing behind");

        // Without --zip, nothing is zipped.
        config.Zip = false;
        result = new BackupEngine().Run(source, history, null, false, config, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && !File.Exists(ListBackups(history)[2] + ".zip"), "no zip unless asked for");
    }
}
