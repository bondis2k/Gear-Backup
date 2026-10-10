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

    // An MTP device in memory: files are path -> content, with a date per file. Counts downloads.
    sealed class FakeMtpSource : IMtpSource
    {
        public readonly List<MtpObject> Items = new List<MtpObject>();
        public readonly Dictionary<string, byte[]> Content = new Dictionary<string, byte[]>();
        public int Downloads;
        public int ShortBy;              // deliver this many bytes fewer than the size, like a broken transfer
        public Action OnDownload;        // e.g. cancel mid-download
        public bool Disconnect;          // fail after writing only part of a temporary download
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
            {
                if (Disconnect)
                {
                    output.Write(data, 0, data.Length / 2);
                    throw new IOException("MTP device disconnected during transfer.");
                }
                output.Write(data, 0, Math.Max(0, data.Length - ShortBy));
            }
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
            Directory.GetFiles(mirror, ".geardownload-*", SearchOption.AllDirectories).Length == 0,
            "a failed download keeps the previous mirror copy and leaves no partial file");

        var cts = new CancellationTokenSource();
        device.OnDownload = () => cts.Cancel();
        bool cancelled = false;
        using (new OperationContext(new TestInteraction(), cts.Token).Activate())
            try { SyncMirror(device, mirror); } catch (OperationCanceledException) { cancelled = true; }
        device.OnDownload = null;
        Assert(cancelled && Directory.GetFiles(mirror, ".geardownload-*", SearchOption.AllDirectories).Length == 0, "cancelling a sync leaves no partial file");

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
        File.Delete(Path.Combine(mirror, "projects", "user", "ziiamed 1.xy"));
        new BackupEngine().Run(mirror, history, backup, true, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = xy });
        bool listed = false;
        foreach (string report in Directory.GetFiles(backup, "backup-statistics_*.txt"))
            listed |= File.ReadAllText(report).Contains("Projects no longer on the OP-XY (1):\r\n      ziiamed 1");
        Assert(listed, "a project removed from the OP-XY is reported");
    }

    static void TestTrackerMini(string dir)
    {
        DeviceProfile tracker = Devices.TrackerMini;
        Assert(Devices.Find("tracker-mini") == tracker && !tracker.Mtp, "the Tracker Mini is a USB drive, not an MTP device");
        string pnp = @"USBSTOR\DISK&VEN_POLYEND&PROD_TRACKER_STORAGE&REV_0001\7&129045C2&0";
        string model = "POLYEND TRACKER STORAGE USB DEVICE";
        Assert(tracker.MatchesDisk(pnp, model) && !Devices.M8.MatchesDisk(pnp, model), "the Tracker Mini's disk is recognised as its own");
        Option[] modes = RestoreModes(tracker);
        Assert(modes.Length == 3 && Array.Exists(modes, m => m.Key == 'p' && m.Label == "Whole project folder"),
            "Tracker Mini restores offer a whole project folder");
        Assert(Array.FindIndex(RestoreModes(Devices.M8), m => m.Key == 'p') < 0, "the M8 has no project folders");
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            OperationContext.Current.Device = tracker;
            Assert(IsSong(@"Projects\User\Jam\project.mt") && IsSong(@"Projects\Artists\Rave\PROJECT.MT"), "Tracker projects are project.mt files");
            Assert(!IsSong(@"Projects\User\Jam\patterns\pattern_01.mtp") && !IsSong(@"Projects\User\Jam\samples\kick.wav"),
                "a project's patterns and samples aren't projects");
            Assert(SongName(@"Projects\User\Jam\project.mt") == @"User\Jam" && SongName(@"Workspace\project.mt") == "Workspace",
                "projects are named after their folder");
        }

        // A Tracker card backs up like the M8's, and summaries name projects by folder.
        string card = Rooted(Path.Combine(dir, "tracker-card"));
        Put(Path.Combine(card, "Projects", "User", "Jam", "project.mt"), "JAM");
        Put(Path.Combine(card, "Projects", "User", "Jam", "patterns", "pattern_01.mtp"), "PATTERN");
        Put(Path.Combine(card, "Projects", "User", "Jam", "samples", "kick.wav"), "KICK");
        Put(Path.Combine(card, "Projects", "User", "Other", "project.mt"), "OTHER");
        Put(Path.Combine(card, "Workspace", "project.mt"), "OPEN");
        string history = Path.Combine(dir, "tracker-history");
        var config = new Config { BackupFolder = dir, Preview = false };
        OperationResult first = new BackupEngine().Run(card, history, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = tracker });
        string backup = ListBackups(history)[0];
        Assert(first.ExitCode == 0 && File.ReadAllText(Path.Combine(backup, "backup-statistics.txt")).Contains("Projects: 3 (first backup"),
            "the first Tracker report counts projects");
        Put(Path.Combine(card, "Projects", "User", "New", "project.mt"), "NEW");
        OperationResult second = new BackupEngine().Run(card, history, backup, true, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = tracker });
        Assert(second.Summary.Contains("Projects: 1 new (New).") &&
            File.ReadAllText(Directory.GetFiles(backup, "backup-statistics_*.txt")[0]).Contains("New projects (1):\r\n      User\\New"),
            "Tracker summaries name new projects by their folder");

        // Restoring a whole project brings back its folder (patterns and samples included) and nothing else.
        string target = Rooted(Path.Combine(dir, "tracker-target"));
        Directory.CreateDirectory(target);
        var ui = new TestInteraction();
        List<string> offered = null;
        ui.OnChooseItem = (question, items) =>
        {
            if (question.StartsWith("Choose a project")) { offered = new List<string>(items); return items.IndexOf(@"Projects\User\Jam") + 1; }
            return question.StartsWith("Restore from which backup") ? 1 : 0;
        };
        ui.Keys.Enqueue('p');
        OperationResult restored;
        using (new OperationContext(ui, CancellationToken.None) { Device = tracker }.Activate())
            restored = RunRestore(target, ListBackups(history), config);
        Assert(offered != null && offered.Count == 4 && offered.Contains("Workspace"), "every project folder is offered, the open one too");
        Assert(restored.ExitCode == 0 && File.ReadAllText(Path.Combine(target, @"Projects\User\Jam\samples\kick.wav")) == "KICK" &&
            File.Exists(Path.Combine(target, @"Projects\User\Jam\patterns\pattern_01.mtp")), "a whole project folder is restored");
        Assert(!Directory.Exists(Path.Combine(target, @"Projects\User\Other")) && !Directory.Exists(Path.Combine(target, "Workspace")),
            "other projects are left alone");
    }

    static void TestX100VI(string dir)
    {
        DeviceProfile camera = Devices.X100VI;
        Assert(Devices.Find("x100vi") == camera && camera.Mtp && camera.MtpVendor == "VID_04CB" && camera.MtpName == "X100VI",
            "the X100VI is an MTP device from Fujifilm's USB vendor");
        Assert(RestoreModes(camera).Length == 2, "X100VI restores offer all files and single file only");
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            OperationContext.Current.Device = camera;
            Assert(IsSong(@"DCIM\100_FUJI\DSCF0689.JPG") && IsSong(@"DCIM\100_FUJI\DSCF0689.RAF") && IsSong(@"DCIM\100_FUJI\DSCF0690.MOV") &&
                IsSong(@"DCIM\100_FUJI\DSCF0691.HIF"), "photos and videos are the camera's songs");
            Assert(SongName(@"DCIM\100_FUJI\DSCF0689.RAF") == @"100_FUJI\DSCF0689", "photo names leave out DCIM and the extension");
        }

        // A shot saved as JPG and RAF counts once, and photos deleted on the camera are reported.
        string mirror = Rooted(Path.Combine(dir, "x100vi-mirror"));
        foreach (string shot in new[] { "DSCF0001", "DSCF0002" })
        {
            Put(Path.Combine(mirror, "DCIM", "100_FUJI", shot + ".JPG"), "JPEG " + shot);
            Put(Path.Combine(mirror, "DCIM", "100_FUJI", shot + ".RAF"), "RAW " + shot);
        }
        string history = Path.Combine(dir, "x100vi-history");
        var config = new Config { BackupFolder = dir, Preview = false };
        OperationResult first = new BackupEngine().Run(mirror, history, null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = camera });
        string backup = ListBackups(history)[0];
        Assert(first.ExitCode == 0 && File.ReadAllText(Path.Combine(backup, "backup-statistics.txt")).Contains("Photos and videos: 2 (first backup"),
            "a JPG and RAF of one shot count as one photo");
        Put(Path.Combine(mirror, "DCIM", "100_FUJI", "DSCF0003.JPG"), "JPEG 3");
        Put(Path.Combine(mirror, "DCIM", "100_FUJI", "DSCF0003.RAF"), "RAW 3");
        File.Delete(Path.Combine(mirror, "DCIM", "100_FUJI", "DSCF0001.JPG"));
        File.Delete(Path.Combine(mirror, "DCIM", "100_FUJI", "DSCF0001.RAF"));
        File.Delete(Path.Combine(mirror, "DCIM", "100_FUJI", "DSCF0002.RAF"));
        OperationResult second = new BackupEngine().Run(mirror, history, backup, true, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = camera });
        string report = File.ReadAllText(Directory.GetFiles(backup, "backup-statistics_*.txt")[0]);
        Assert(second.Summary.Contains("Photos and videos: 1 new (DSCF0003)."), "a new shot is named once: " + second.Summary);
        Assert(report.Contains("Photos and videos no longer on the X100VI (1):\r\n      100_FUJI\\DSCF0001"),
            "a shot deleted on the camera is reported, one with a file left isn't");
        Assert(File.Exists(Path.Combine(backup, @"DCIM\100_FUJI\DSCF0001.RAF")), "deleted photos stay in the backup");

        // Photos going back onto the camera: refused over USB with the reason, and confirmed onto a card.
        Assert(MtpRestoreUnsupported(camera).Contains("rarely put back on a camera") && !MtpRestoreUnsupported(Devices.OPXY).Contains("rarely"),
            "restoring to the camera explains why it isn't offered");
        Assert(HistoryDevice(backup) == camera && HistoryDevice(Path.Combine(dir, "card-1234ABCD", "2026-01-01")) == null,
            "a backup's device comes from its history's name");
        string folder = Rooted(Path.Combine(dir, "photos-out"));
        TestInteraction asked = WithUi(() => Assert(ConfirmRestoreTarget(backup, folder), "restoring photos to a folder needs no confirmation"));
        Assert(asked.Questions.Count == 0, "no question for a folder");
        bool declined = true, accepted = false;
        asked = WithUi(() => declined = ConfirmRestoreTarget(backup, @"Z:\"), 'n');
        Assert(!declined && asked.Output.ToString().Contains(@"Z:\ is a memory card") && asked.Questions[0].Contains("anyway?"),
            "restoring photos onto a memory card warns and can be declined");
        WithUi(() => accepted = ConfirmRestoreTarget(backup, @"Z:\"), 'y');
        Assert(accepted, "and can be confirmed");
        string m8Backup = Path.Combine(dir, "m8-card-1234ABCD", "2026-01-01");
        asked = WithUi(() => Assert(ConfirmRestoreTarget(m8Backup, @"Z:\"), "restoring an M8 backup onto a card isn't questioned"));
        Assert(asked.Questions.Count == 0, "no question for the M8");
    }

    // Histories are named per device, and older histories keep working.
    static void TestProfileNames(string dir)
    {
        string source = Rooted(Path.Combine(dir, "named-source"));
        Directory.CreateDirectory(source);
        string key = SourceKey(source);
        string profiles = Path.Combine(dir, "named-histories", "profiles");
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.TrackerMini }.Activate())
        {
            Assert(key.StartsWith("folder-") && ProfileId(source, "") == "tracker-mini-" + key, "a history name starts with its device");
            Assert(ProfileFolder(profiles, source, "") == Path.Combine(profiles, "tracker-mini-" + key), "a new source gets a device-named history");

            // A history from before device names is renamed, but not by a preview.
            string old = Path.Combine(profiles, key, "2026-01-01");
            Directory.CreateDirectory(old);
            OperationContext.Current.PreviewOnly = true;
            Assert(ProfileFolder(profiles, source, "") == Path.Combine(profiles, key), "a preview uses the old history without renaming it");
            OperationContext.Current.PreviewOnly = false;
            Assert(ProfileFolder(profiles, source, "") == Path.Combine(profiles, "tracker-mini-" + key) &&
                Directory.Exists(Path.Combine(profiles, "tracker-mini-" + key, "2026-01-01")) && !Directory.Exists(Path.Combine(profiles, key)),
                "an old history is renamed to the device-named one");
            Assert(((TestInteraction)OperationContext.Current.Interaction).Output.ToString().Contains("renamed to tracker-mini-" + key), "the rename is reported");

            Assert(ProfileFolder(profiles, source, "studio") == Path.Combine(profiles, "studio"), "a named profile is used as it is");
        }
        // The same card taken for another device keeps its one history.
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.M8 }.Activate())
            Assert(ProfileFolder(profiles, source, "") == Path.Combine(profiles, "tracker-mini-" + key), "a source keeps its history under another device's name");
    }

    static void TestOp1FieldBackup(string dir)
    {
        Assert(Devices.Find("op1-field") == Devices.OP1Field && Devices.OP1Field.Mtp && !Devices.M8.Mtp, "the OP-1 Field is an MTP device");
        Option[] modes = RestoreModes(Devices.OP1Field);
        Assert(modes.Length == 2, "OP-1 Field restores offer all files and single file only");
        Assert(Devices.Names == "M8, OP-1 Field, OP-XY, Tracker Mini or X100VI", "messages name every supported device");

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
        string trackerCard = Rooted(Path.Combine(dir, "tracker-card-in-reader"));
        Put(Path.Combine(trackerCard, "trackerConfig"), "EEPROM");
        Assert(DeviceFor(new Config(), null, trackerCard) == Devices.TrackerMini, "a Tracker card in a reader is recognised by its trackerConfig");
        Assert(DeviceFor(new Config(), Devices.M8, trackerCard) == Devices.M8 && DeviceFor(new Config { Device = "m8" }, null, trackerCard) == Devices.M8,
            "detection and the Device setting win over the marker file");
        Assert(DeviceFor(new Config(), null, Rooted(dir)) == Devices.M8, "a folder without a marker file is the default device");
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

    static void TestAdditionalMirrorLinks(string dir)
    {
        string root = Folder(dir, "extra-mirror-links");
        string outside = Folder(root, "outside");
        string victim = Path.Combine(outside, "keep.txt");
        Put(victim, "UNRELATED");
        string linkedRoot = Path.Combine(root, "root-link");
        CreateTestJunction(linkedRoot, outside);
        try
        {
            Throws(() => WithUi(() => SyncMirror(new FakeMtpSource(), linkedRoot)), "a linked mirror root is rejected");
            Assert(File.ReadAllText(victim) == "UNRELATED", "linked mirror root never deletes its target content");
        }
        finally { Directory.Delete(linkedRoot); }

        string mirror = Folder(root, "mirror");
        string stale = Path.Combine(mirror, "stale.txt");
        Put(stale, "PRESERVE");
        string missing = Folder(root, "missing-target");
        string broken = Path.Combine(mirror, "broken-folder");
        CreateTestJunction(broken, missing);
        Directory.Delete(missing);
        try
        {
            Throws(() => WithUi(() => SyncMirror(new FakeMtpSource(), mirror)), "a broken folder link is rejected");
            Assert(File.Exists(stale) && !Directory.Exists(missing), "broken link rejection neither deletes stale files nor recreates its target");
        }
        finally { Directory.Delete(broken); }

    }

    static void TestMirrorFileSymlinks(string dir)
    {
        string root = Folder(dir, "mirror-file-symlinks");
        string mirror = Folder(root, "mirror");
        string outside = Folder(root, "outside");
        string victim = Path.Combine(outside, "keep.txt");
        Put(victim, "UNRELATED");
        string stale = Path.Combine(mirror, "stale.txt");
        Put(stale, "PRESERVE");
        string fileLink = Path.Combine(mirror, "file-link.txt");
        CreateTestFileSymlink(fileLink, victim);
        try
        {
            Throws(() => WithUi(() => SyncMirror(new FakeMtpSource(), mirror)), "a file symbolic link is rejected before cleanup");
            Assert(File.ReadAllText(victim) == "UNRELATED" && File.Exists(stale), "file link rejection preserves both the target and stale mirror files");
        }
        finally { File.Delete(fileLink); }
        CreateTestFileSymlink(fileLink, Path.Combine(outside, "absent.txt"));
        try
        {
            Throws(() => WithUi(() => SyncMirror(new FakeMtpSource(), mirror)), "a broken file symbolic link is rejected");
            Assert(File.Exists(stale) && !File.Exists(Path.Combine(outside, "absent.txt")), "broken file link rejection makes no changes");
        }
        finally { File.Delete(fileLink); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)]
    static extern bool CreateTestSymbolicLink(string link, string target, uint flags);

    static void CreateTestFileSymlink(string link, string target)
    {
        bool created = CreateTestSymbolicLink(link, target, 2); // Allow unprivileged creation in Developer Mode.
        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (!created && error == 87) // Older Windows does not support this flag.
        {
            created = CreateTestSymbolicLink(link, target, 0);
            error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
        if (!created && error == 1314)
            throw new TestSkippedException("Windows requires Developer Mode or symbolic-link privilege for this test. Junction tests still run.");
        Assert(created, "test file symbolic link created; Windows error " + error);
        Assert((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "fixture is a symbolic link, not a regular file");
    }

    static void TestMtpDisconnect(string dir)
    {
        string mirror = Folder(dir, "disconnect-mirror");
        var day = new DateTime(2026, 1, 1, 10, 0, 0);
        var device = new FakeMtpSource();
        device.File("sound.wav", "OLD SOUND", day);
        WithUi(() => SyncMirror(device, mirror));
        string old = Path.Combine(mirror, "sound.wav");
        string stale = Path.Combine(mirror, "stale.wav");
        Put(stale, "KEEP UNTIL SYNC SUCCEEDS");
        string staleFolder = Folder(mirror, "stale-empty");
        device.File("sound.wav", "NEW SOUND FROM DEVICE", day.AddDays(1));
        device.Disconnect = true;
        Throws(() => WithUi(() => SyncMirror(device, mirror)), "disconnect after a partial download aborts sync");
        Assert(device.Downloads == 2 && File.ReadAllText(old) == "OLD SOUND" && File.GetLastWriteTime(old) == day,
            "disconnect preserves the previous mirror contents and date");
        Assert(File.Exists(stale) && Directory.Exists(staleFolder), "disconnect prevents stale file and folder cleanup");
        Assert(Directory.GetFiles(mirror, ".geardownload-*", SearchOption.AllDirectories).Length == 0,
            "disconnect removes the partial temporary download");
        device.Disconnect = false;
        MirrorResult result = null;
        WithUi(() => { result = SyncMirror(device, mirror); });
        Assert(result.Downloaded == 1 && result.Removed == 1 && File.ReadAllText(old) == "NEW SOUND FROM DEVICE" &&
            !Directory.Exists(staleFolder), "reconnecting retries the failed file and performs cleanup only after success");
    }

    static void TestPartialExactRestore(string dir)
    {
        Fixture f = BackedUpCard(Folder(dir, "partial-exact-fixture"), false);
        string target = Rooted(Folder(dir, "partial-exact-target"));
        Put(Path.Combine(target, "Songs", "one.m8s"), "OLD ONE");
        Put(Path.Combine(target, "Songs", "removed.m8s"), "OLD TWO");
        Put(Path.Combine(target, "extras", "keep.txt"), "UNRELATED EXTRA");
        string empty = Folder(target, "extra-empty");
        var ui = new TestInteraction();
        FileStream locked = null;
        ui.OnProgress = p => {
            if (p.Stage == Stage.Restoring && p.Path == @"Songs\removed.m8s")
                locked = new FileStream(Path.Combine(target, p.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        var copies = new List<RestoreEntry> { f.First.Files.Find(e => e.Relative == @"Songs\one.m8s"),
            f.First.Files.Find(e => e.Relative == @"Songs\removed.m8s") };
        OperationResult result;
        try
        {
            result = new RestoreEngine().RunSelected(target, f.First, copies, 'a', null, true,
                new Config { Preview = false, Verify = true, Thorough = true, Recovery = false }, new OperationContext(ui, CancellationToken.None));
        }
        finally { if (locked != null) locked.Dispose(); }
        Assert(result.ExitCode != 0 && !result.Cancelled && result.Summary.Contains("1 failed"),
            "exact restore reports a failed replacement after a successful copy: " + result.Summary + " " + ui.Output);
        Assert(File.ReadAllText(Path.Combine(target, @"Songs\one.m8s")) == "AAAA" &&
            File.ReadAllText(Path.Combine(target, @"Songs\removed.m8s")) == "OLD TWO", "partial restore keeps the completed copy and preserves the failed target");
        Assert(File.ReadAllText(Path.Combine(target, @"extras\keep.txt")) == "UNRELATED EXTRA" && Directory.Exists(empty),
            "partial exact restore never deletes extra files or empty folders");
        Assert(Directory.GetFiles(target, ".gearrestore-*", SearchOption.AllDirectories).Length == 0,
            "failed exact replacement removes the staged temporary file");
    }

    static void TestMalformedSnapshots(string dir)
    {
        string backup = Folder(dir, "malformed-backup");
        string manifest = Path.Combine(backup, "input.manifest");
        string hash = new string('a', 64);
        string valid = "F\t" + Encode(@"Songs\one.m8s") + "\t" + hash + "\t4\t0\t0";
        string[] invalid = {
            "", "GEARSNAPSHOT\t99\n", AppIdentity.SnapshotHeader + "\nF",
            AppIdentity.SnapshotHeader + "\nF\t" + Encode("one.m8s") + "\t" + hash + "\t4\t0",
            AppIdentity.SnapshotHeader + "\nD\t%%%",
            AppIdentity.SnapshotHeader + "\nD\t" + Encode(@"..\outside.txt"),
            AppIdentity.SnapshotHeader + "\nD\t" + Encode(@"C:\outside.txt"),
            AppIdentity.SnapshotHeader + "\n" + valid + "\n" + valid,
            AppIdentity.SnapshotHeader + "\n" + valid + "\nD\t" + Encode("songs/ONE.m8s"),
            AppIdentity.SnapshotHeader + "\nF\t" + Encode("Songs") + "\t" + hash + "\t4\t0\t0\n" + valid,
            AppIdentity.SnapshotHeader + "\n" + valid.Replace("\t4\t", "\t-1\t"),
            AppIdentity.SnapshotHeader + "\n" + valid.Replace("\t4\t", "\t999999999999999999999\t"),
            AppIdentity.SnapshotHeader + "\n" + valid.Replace("\t4\t", "\tnot-a-number\t"),
            AppIdentity.SnapshotHeader + "\n" + valid.Replace("\t0\t0", "\t-1\t0"),
            AppIdentity.SnapshotHeader + "\n" + valid.Replace("\t0\t0", "\t0\t3155378976000000000"),
            AppIdentity.SnapshotHeader + "\n" + valid.Replace(hash, "bad-checksum")
        };
        for (int i = 0; i < invalid.Length; i++)
        {
            Put(manifest, invalid[i]);
            byte[] before = File.ReadAllBytes(manifest);
            WithUi(() => Throws(() => ReadSnapshot(backup, manifest), "malformed snapshot rejected: case " + i));
            Assert(Convert.ToBase64String(File.ReadAllBytes(manifest)) == Convert.ToBase64String(before) &&
                Directory.GetFileSystemEntries(backup).Length == 1, "rejecting malformed input leaves the manifest unchanged and creates no objects");
        }
        Put(manifest, AppIdentity.SnapshotHeader + "\nD\t" + Encode("Songs") + "\n" + valid + "\n");
        Snapshot read = null;
        WithUi(() => read = ReadSnapshot(backup, manifest));
        Assert(read.Files.Count == 1 && read.Folders.Count == 1 && read.Files[0].Size == 4,
            "valid manifest remains accepted with a final newline and zero dates");
    }

    static void TestMirrorLinks(string dir)
    {
        string mirror = Folder(dir, "linked-mirror");
        string outside = Folder(dir, "mirror-victim");
        string victim = Path.Combine(outside, "keep.txt");
        Put(victim, "UNRELATED");
        string stale = Path.Combine(mirror, "stale.txt");
        Put(stale, "KEEP UNTIL VALIDATED");
        string link = Path.Combine(Folder(mirror, "nested"), "linked");
        var device = new FakeMtpSource();
        device.File("new.txt", "NEW", DateTime.Now);
        CreateTestJunction(link, outside);
        try
        {
            Throws(() => WithUi(() => SyncMirror(device, mirror)), "a stale nested junction aborts mirror sync");
            Assert(File.ReadAllText(victim) == "UNRELATED" && File.Exists(stale) && device.Downloads == 0,
                "mirror links are rejected before downloads or deletions, preserving outside files");
        }
        finally { Directory.Delete(link); }

        // Simulate a link planted after the initial scan; cleanup must check again without cached paths.
        device.OnDownload = () => CreateTestJunction(link, outside);
        try
        {
            Throws(() => WithUi(() => SyncMirror(device, mirror)), "cleanup rejects a link added during download");
            Assert(File.ReadAllText(victim) == "UNRELATED" && File.Exists(stale),
                "cleanup validates the whole tree before removing stale files");
        }
        finally { device.OnDownload = null; if (Directory.Exists(link)) Directory.Delete(link); }
        MirrorResult result = null;
        WithUi(() => { result = SyncMirror(device, mirror); });
        Assert(result.Removed == 1 && !File.Exists(stale) && File.Exists(Path.Combine(mirror, "new.txt")),
            "ordinary mirror cleanup still works after removing the link");
    }

    static void CreateTestJunction(string link, string target)
    {
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert(process.ExitCode == 0 && Directory.Exists(link), "test junction created");
        }
    }

    static void TestAtomicText(string dir)
    {
        string folder = Folder(dir, "atomic-text");
        string victim = Path.Combine(dir, "atomic-victim.txt");
        Put(victim, "UNRELATED");
        string target = Path.Combine(folder, "status.txt");
        Assert(TryHardLink(victim, target + ".tmp"), "planted temporary hard link created");
        WithUi(() => AtomicText(target, "complete\n"));
        Assert(File.ReadAllText(victim) == "UNRELATED" && File.ReadAllText(target + ".tmp") == "UNRELATED",
            "atomic text ignores a planted predictable temporary hard link");
        Assert(File.ReadAllText(target) == "complete\n" && File.ReadAllBytes(target)[0] == (byte)'c',
            "new text files preserve content and UTF-8 without a BOM");
        File.Delete(target);
        Assert(TryHardLink(victim, target), "existing target hard link created");
        WithUi(() => AtomicText(target, "updated"));
        Assert(File.ReadAllText(target) == "updated" && File.ReadAllText(victim) == "UNRELATED",
            "replacement changes the target name without truncating its hard-linked original");
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            Throws(() => WithUi(() => AtomicText(target, "FAILED")), "locked atomic replacement fails");
        Assert(File.ReadAllText(target) == "updated" && Directory.GetFiles(folder, ".geartext-*").Length == 0,
            "failed text replacement keeps the original and cleans its unique temporary file");
        var cts = new CancellationTokenSource();
        cts.Cancel();
        bool cancelled = false;
        using (new OperationContext(new TestInteraction(), cts.Token).Activate())
            try { AtomicText(target, "CANCELLED"); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "cancelled text write is refused");
        Assert(File.ReadAllText(target) == "updated" && Directory.GetFiles(folder, ".geartext-*").Length == 0,
            "cancellation leaves the text and temporary folder unchanged");
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
