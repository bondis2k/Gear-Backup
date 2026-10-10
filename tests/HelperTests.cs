// Focused tests for the helpers the backup, restore and check workflows depend on: change summaries,
// name warnings, file classification, comparisons, path safety, checksum lists, settings, retention,
// the resume question and the zip's failure paths. Each runs as a named test; see RunTests in TestRunner.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

partial class M8Backup
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
        RunTest("Console questions", TestConsoleQuestions);
        RunTest("Device profiles", () => TestDevices(dir));
        RunTest("OP-1 Field: MTP mirror", () => TestMtpMirror(dir));
        RunTest("OP-1 Field: backup of a mirrored device", () => TestOp1FieldBackup(dir));
        RunTest("OP-XY: projects and backup", () => TestOpXy(dir));
    }

    // An MTP device in memory: files are path -> content, with a date per file. Counts downloads.
    sealed class FakeMtpSource : IMtpSource
    {
        public readonly List<MtpObject> Items = new List<MtpObject>();
        public readonly Dictionary<string, byte[]> Content = new Dictionary<string, byte[]>();
        public int Downloads;
        public int ShortBy;              // deliver this many bytes fewer than the size, like a broken transfer
        public Action OnDownload;        // e.g. cancel mid-download
        public void Folder(string path) { Items.Add(new MtpObject { Id = path, Name = Path.GetFileName(path), RelativePath = path, IsFolder = true }); }
        public void File(string path, string text, DateTime modified)
        {
            Items.RemoveAll(i => i.RelativePath == path);
            Content[path] = System.Text.Encoding.UTF8.GetBytes(text);
            Items.Add(new MtpObject { Id = path, Name = Path.GetFileName(path), RelativePath = path, Size = Content[path].Length, Modified = modified });
        }
        public List<MtpObject> ListAll() { return new List<MtpObject>(Items); }
        public void Download(MtpObject item, string to, Action<long> progress)
        {
            Downloads++;
            if (OnDownload != null) OnDownload();
            byte[] data = Content[item.Id];
            using (var output = new FileStream(to, FileMode.CreateNew))
                output.Write(data, 0, Math.Max(0, data.Length - ShortBy));
            if (progress != null) progress(data.Length);
        }
    }

    static void TestMtpMirror(string dir)
    {
        string mirror = Path.Combine(dir, "op1-mirror");
        var day = new DateTime(2026, 1, 1, 10, 0, 0);
        var device = new FakeMtpSource();
        device.Folder("synth"); device.Folder(@"synth\slots"); device.Folder(@"synth\slots\1"); device.Folder("tapes"); device.Folder(@"tapes\tape 01");
        device.Folder("empty");
        device.File(@"synth\slots\1\lullaby.aif", "AIFF-1", day);
        device.File(@"tapes\tape 01\tape.json", "{\"loop\":false}", day);
        device.File(@"..\evil.txt", "escape", day);
        device.File("bad:name.txt", "stream", day);
        device.File("trailing.", "dot", day);

        MirrorResult result = null;
        WithUi(() => { result = SyncMirror(device, mirror); });
        Assert(result.Downloaded == 2 && result.Unchanged == 0, "first sync downloads every file");
        Assert(File.ReadAllText(Path.Combine(mirror, @"synth\slots\1\lullaby.aif")) == "AIFF-1" &&
            Directory.Exists(Path.Combine(mirror, "empty")), "the mirror holds the device's files and folders");
        Assert(File.GetLastWriteTime(Path.Combine(mirror, @"tapes\tape 01\tape.json")) == day, "mirrored files keep the device's dates");
        Assert(result.Skipped.Count == 3 && !File.Exists(Path.Combine(dir, "evil.txt")), "names that aren't safe on Windows are skipped, never written outside the mirror");

        WithUi(() => { result = SyncMirror(device, mirror); });
        Assert(result.Downloaded == 0 && result.Unchanged == 2, "an unchanged device downloads nothing");

        device.File(@"synth\slots\1\lullaby.aif", "AIFF-1-EDITED", day);
        device.File(@"tapes\tape 01\tape.json", "{\"loop\":true!}", day.AddMinutes(5));
        WithUi(() => { result = SyncMirror(device, mirror); });
        Assert(result.Downloaded == 2 && File.ReadAllText(Path.Combine(mirror, @"synth\slots\1\lullaby.aif")) == "AIFF-1-EDITED",
            "files with a new size or date are downloaded again");

        device.Items.RemoveAll(i => i.RelativePath.StartsWith("tapes"));
        WithUi(() => { result = SyncMirror(device, mirror); });
        Assert(result.Removed == 1 && !Directory.Exists(Path.Combine(mirror, "tapes")), "files and folders gone from the device leave the mirror");
        Assert(Directory.Exists(Path.Combine(mirror, "empty")), "empty folders that are still on the device stay");

        device.File(@"synth\slots\1\lunar.aif", "OLD FACTORY SOUND", new DateTime(1970, 3, 30, 11, 3, 43));
        WithUi(() => SyncMirror(device, mirror));
        WithUi(() => { result = SyncMirror(device, mirror); });
        Assert(result.Downloaded == 0, "a file dated before 1980 (as some OP-1 factory sounds are) isn't downloaded again");
        device.Items.RemoveAll(i => i.RelativePath == @"synth\slots\1\lunar.aif");

        device.File(@"synth\undated.aif", "U", DateTime.MinValue);
        WithUi(() => SyncMirror(device, mirror));
        int before = device.Downloads;
        WithUi(() => SyncMirror(device, mirror));
        Assert(device.Downloads == before + 1, "a file without a date is always downloaded again, since it can't be compared");

        device.File(@"synth\slots\1\lullaby.aif", "AIFF-2-BROKEN", day.AddDays(1));
        device.ShortBy = 3;
        Throws(() => WithUi(() => SyncMirror(device, mirror)), "an incomplete download is an error");
        device.ShortBy = 0;
        Assert(File.ReadAllText(Path.Combine(mirror, @"synth\slots\1\lullaby.aif")) == "AIFF-1-EDITED" &&
            Directory.GetFiles(mirror, ".m8download-*", SearchOption.AllDirectories).Length == 0,
            "a failed download keeps the previous mirror copy and leaves no partial file");

        var cts = new CancellationTokenSource();
        device.OnDownload = () => cts.Cancel();
        bool cancelled = false;
        using (new OperationContext(new TestInteraction(), cts.Token).Activate())
            try { SyncMirror(device, mirror); } catch (OperationCanceledException) { cancelled = true; }
        device.OnDownload = null;
        Assert(cancelled && Directory.GetFiles(mirror, ".m8download-*", SearchOption.AllDirectories).Length == 0, "cancelling a sync leaves no partial file");

        var info = new MtpDeviceInfo { InstanceId = @"USB\VID_2367&PID_0102\L1PRC121", FriendlyName = "OP-1" };
        Assert(info.Serial == "L1PRC121" && info.InterfacePath == @"\\?\usb#vid_2367&pid_0102#l1prc121#{6ac27878-a6fa-4155-ba85-f98f491d4f33}",
            "the device's Windows path is built from its USB instance ID");
        string folder = MirrorFolder(Devices.OP1Field, new MtpDeviceInfo { InstanceId = @"USB\VID_2367&PID_0102\L1:PR*C1" });
        Assert(Path.GetFileName(folder) == "op1-field-L1PRC1", "serial numbers are cleaned for the mirror folder name");
        Assert(ProfileId(Path.Combine(MirrorRoot, "op1-field-L1PRC121"), "") == "op1-field-L1PRC121", "a mirrored device's history is named after it");
    }

    static void TestOpXy(string dir)
    {
        DeviceProfile xy = Devices.OPXY;
        Assert(Devices.Find("op-xy") == xy && xy.Mtp && xy.MtpName == "OP-XY" && Devices.OP1Field.MtpName == "OP-1",
            "the OP-XY is its own MTP device, told apart from the OP-1 Field by name");
        Assert(RestoreModes(xy).Length == 2, "OP-XY restores offer all files and single file only");
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            OperationContext.Current.Device = xy;
            Assert(IsSong(@"projects\user\ziiamed 1.xy") && IsSong(@"projects\workspace.xy"), "OP-XY projects are .xy files");
            Assert(!IsSong(@"projects\user\backups\ziiamed 2.xy\0001.xy"), "the OP-XY's automatic project backups aren't listed as projects");
            Assert(!IsSong(@"samples\user\kick.wav"), "samples aren't projects");
            Assert(SongName(@"projects\user\ziiamed 1.xy") == "ziiamed 1", "project names leave out the projects folder");
        }

        // A mirrored OP-XY backs up like any folder, and its summary talks about projects.
        string mirror = Rooted(Path.Combine(dir, "opxy-mirror"));
        Put(Path.Combine(mirror, "projects", "user", "ziiamed 1.xy"), "PROJECT 1");
        Put(Path.Combine(mirror, "projects", "user", "backups", "ziiamed 1.xy", "0000.xy"), "AUTO BACKUP");
        Put(Path.Combine(mirror, "samples", "user", "kick.wav"), "WAVE");
        string history = Path.Combine(dir, "opxy-history");
        var config = new Config { BackupFolder = dir, Preview = false };
        OperationResult first = new BackupEngine().Run(mirror, history, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = xy });
        string backup = ListBackups(history)[0];
        Assert(first.ExitCode == 0 && File.Exists(Path.Combine(backup, "projects", "user", "backups", "ziiamed 1.xy", "0000.xy")),
            "the OP-XY's automatic project backups are still backed up");
        Assert(File.ReadAllText(Path.Combine(backup, "backup-statistics.txt")).Contains("Projects: 1 (first backup"),
            "the first OP-XY report counts projects");
        Put(Path.Combine(mirror, "projects", "user", "ziiamed 2.xy"), "PROJECT 2");
        OperationResult second = new BackupEngine().Run(mirror, history, backup, true, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = xy });
        Assert(second.Summary.Contains("Projects: 1 new (ziiamed 2)."), "OP-XY summaries name new projects");
    }

    static void TestOp1FieldBackup(string dir)
    {
        Assert(Devices.Find("op1-field") == Devices.OP1Field && Devices.OP1Field.Mtp && !Devices.M8.Mtp, "the OP-1 Field is an MTP device");
        Option[] modes = RestoreModes(Devices.OP1Field);
        Assert(modes.Length == 2, "OP-1 Field restores offer all files and single file only");
        Assert(Devices.Names == "M8, OP-1 Field or OP-XY", "messages name every supported device");

        // The engine backs up a mirror like any folder, with OP-1 wording in the report.
        string mirror = Rooted(Path.Combine(dir, "op1-engine-mirror"));
        Put(Path.Combine(mirror, "synth", "slots", "1", "lullaby.aif"), "AIFF");
        Put(Path.Combine(mirror, "mixdown", "side_a.wav"), "WAVE");
        Put(Path.Combine(mirror, "synth", "a very long name that the M8 would warn about because it is far too long.aif"), "X");
        string history = Path.Combine(dir, "op1-history");
        var ui = new TestInteraction();
        var context = new OperationContext(ui, CancellationToken.None) { Device = Devices.OP1Field };
        var config = new Config { BackupFolder = dir, Preview = false };
        OperationResult first = new BackupEngine().Run(mirror, history, null, false, config, context);
        string backup = ListBackups(history)[0];
        string report = File.ReadAllText(Path.Combine(backup, "backup-statistics.txt"));
        Assert(first.ExitCode == 0 && File.Exists(Path.Combine(backup, "mixdown", "side_a.wav")), "a mirrored OP-1 Field backs up like any folder");
        Assert(report.Contains("Free space on OP-1 Field:  not available over MTP") && !report.Contains("Names the"),
            "the report names the OP-1 Field, without drive space or M8 name warnings");
        Put(Path.Combine(mirror, "mixdown", "side_b.wav"), "WAVE B");
        OperationResult second = new BackupEngine().Run(mirror, history, backup, true, config, new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.OP1Field });
        Assert(second.Summary.Contains("1 new, 0 changed file(s)."), "OP-1 Field summaries count files, not songs");
    }

    static void TestDevices(string dir)
    {
        // The M8 profile holds what used to be spread around the code.
        Assert(Devices.Find("m8") == Devices.M8 && Devices.Find("M8") == Devices.M8 && Devices.Find("op-9") == null, "devices are found by id");
        Assert(Devices.All[0] == Devices.M8, "the M8 is the first and default device");
        Assert(Devices.M8.MatchesDisk(@"USBSTOR\DISK&VEN_M8&PROD_X", "") && Devices.M8.MatchesDisk(@"USBSTOR\DISK&VEN_X", "M8 DISK"),
            "the M8 is recognised by USB vendor or model");
        Assert(!Devices.M8.MatchesDisk(@"USBSTOR\DISK&VEN_SANDISK", "CRUZER"), "other USB disks aren't the M8");
        Assert(new OperationContext(new TestInteraction(), CancellationToken.None).Device == Devices.M8, "operations default to the M8");

        // Which device a source is: a named Device setting wins, then detection, then the default.
        var other = new DeviceProfile { Id = "other", Name = "Other Synth", SongExtensions = new[] { ".song" }, SongsFolder = "projects" };
        Assert(DeviceFor(new Config { Device = "m8" }, other) == Devices.M8, "a named Device setting wins over detection");
        Assert(DeviceFor(new Config(), other) == other && DeviceFor(new Config(), null) == Devices.M8, "auto uses the detected device, else the default");
        var config = new Config();
        TestInteraction ui = WithUi(() => { ApplySetting(config, "Device", "M8"); ApplySetting(config, "Device", "toaster"); });
        Assert(config.Device == "m8" && ui.Output.ToString().Contains("Device=toaster isn't valid"), "the Device setting accepts known devices only");

        // With another device current, everything device-specific follows it.
        Option[] m8Modes = RestoreModes(Devices.M8), otherModes = RestoreModes(other);
        Assert(m8Modes.Length == 4, "the M8 offers all four restore choices");
        Assert(otherModes.Length == 2 && Array.TrueForAll(otherModes, o => o.Key == 'a' || o.Key == 's'),
            "a device without bundles or a song reader only offers all files and single file");
        using (var context = new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            OperationContext.Current.Device = other;
            Assert(IsSong(@"projects\a.song") && !IsSong(@"Songs\a.m8s"), "songs are the current device's song files");
            Assert(SongName(@"projects\live\jam.song") == @"live\jam", "song names drop the current device's songs folder");
            Throws(() => SongDependencies(new RestoreEntry { Relative = "projects\\a.song", StoredPath = Path.Combine(dir, "nope.song") }, new Snapshot(), dir, false),
                "song-with-samples restore is refused for a device without a song reader");

            string source = Rooted(Path.Combine(dir, "other-card"));
            Put(Path.Combine(source, "projects", "new.song"), "N");
            Put(Path.Combine(source, "projects", "old.m8s"), "not a song here");
            var scan = new ScanResult();
            CollectFiles(source, scan);
            var states = new FileState[scan.Files.Count];
            string baseline = Path.Combine(dir, "other-baseline");
            Directory.CreateDirectory(baseline);
            Changes changes = FindChanges(scan, source, states, baseline);
            Assert(changes.NewSongs.Count == 1 && changes.NewSongs[0] == "new" && changes.OtherNew == 1,
                "the change summary counts the current device's songs");
        }
        Assert(OperationContext.Current.Device == Devices.M8, "the device setting ends with its operation");
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

    static void TestSecurity(string dir)
    {
        // 1. A cfgmgr32.dll planted next to the program must not be loaded. The fake is garbage, so
        //    loading it would throw; with DLLs taken from System32 only, the bogus device is just "not found".
        string planted = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cfgmgr32.dll");
        File.WriteAllText(planted, "not a real DLL");
        string error;
        try { error = EjectDrive(@"USB\VID_0000&PID_0000\NOT-A-REAL-DEVICE"); }
        finally { File.Delete(planted); }
        Assert(error == "the M8 device was not found.", "a DLL planted next to the program is ignored; System32's is used");

        // 2. A link named like a backup is never listed, so KeepBackups can't offer it for deletion.
        string history = Path.Combine(dir, "link-history");
        Directory.CreateDirectory(Path.Combine(history, "2026-10-02"));
        string elsewhere = Path.Combine(dir, "link-elsewhere");
        Put(Path.Combine(elsewhere, "keep.txt"), "must survive");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c mklink /J \"" + Path.Combine(history, "2026-10-01") + "\" \"" + elsewhere + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        mklink.WaitForExit();
        Assert(Directory.Exists(Path.Combine(history, "2026-10-01")), "test junction created");
        List<string> backups = ListBackups(history);
        Assert(backups.Count == 1 && Path.GetFileName(backups[0]) == "2026-10-02", "a link named like a backup isn't treated as one");
        TestInteraction ui = WithUi(() => PruneBackups(history, 1, backups[0]), 'n');
        Assert(ui.Questions.Count == 0 && File.Exists(Path.Combine(elsewhere, "keep.txt")), "KeepBackups never offers a linked folder");
        Directory.Delete(Path.Combine(history, "2026-10-01"));  // removes only the junction

        // 3. A huge file posing as a song is refused before being read into memory.
        string huge = Path.Combine(dir, "huge.m8s");
        using (var stream = new FileStream(huge, FileMode.Create)) stream.SetLength(MaxSongSize + 1);
        string refusal = null;
        try { SamplePaths(huge); } catch (IOException ex) { refusal = ex.Message; }
        Assert(refusal != null && refusal.Contains("too large"), "an oversized song file is refused by size, before reading it");
        File.Delete(huge);

        // 4. The zip's copy of the statistics hides local paths; the backup's own copy keeps them.
        string source = Rooted(Path.Combine(dir, "redact-card"));
        Put(Path.Combine(source, "Songs", "a.m8s"), "A");
        string zipHistory = Path.Combine(dir, "redact-history");
        new BackupEngine().Run(source, zipHistory, null, false, new Config { BackupFolder = dir, Preview = false, Zip = true },
            new OperationContext(new TestInteraction(), CancellationToken.None));
        string backup = ListBackups(zipHistory)[0];
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string zipped;
        using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(backup + ".zip"), System.IO.Compression.ZipArchiveMode.Read))
        using (var reader = new StreamReader(zip.GetEntry("backup-statistics.txt").Open()))
            zipped = reader.ReadToEnd();
        Assert(zipped.Contains("<this backup>") && zipped.IndexOf(backup, StringComparison.OrdinalIgnoreCase) < 0,
            "the zip's statistics hide the backup folder's path");
        Assert(!source.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ||
            (zipped.Contains("%USERPROFILE%") && zipped.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0),
            "the zip's statistics hide the user profile path");
        Assert(File.ReadAllText(Path.Combine(backup, "backup-statistics.txt")).Contains(backup),
            "the backup's own statistics keep full paths");
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
        Assert(IsBackupMetaFile(@"b\.m8backup\objects\abc") && IsBackupMetaFile(@"b\checksums.sha256.tmp") && !IsBackupMetaFile(@"b\Songs\a.m8s"),
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
        Assert(!Within(@"C:\Music\M8 Backups2", @"C:\Music\M8 Backups") && Within(@"C:\Music\M8 Backups\x", @"C:\Music\M8 Backups"),
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
            SourceFolder = Path.Combine(dir, "card"), Profile = "Studio", Thorough = true, Preview = false, HardLinks = true, Zip = true };
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
        Throws(() => HistoryFor(paths, Path.Combine(dir, "card-inside", "M8 Backups", "card")), "a source inside the backup location is rejected");
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
