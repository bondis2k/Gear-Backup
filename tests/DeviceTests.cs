// Device profiles and the devices themselves: detection data, the MTP mirror, and each
// device's songs, projects or photos, history names and restore choices.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static FileCopy;
using static PathSafety;
using static Snapshots;
using static Backups;
using static ChangeSummary;
using static RestoreFlow;
using static SampleDependencies;
using static MtpMirror;
using static PortableDevices;
using static AppSettings;
using static DeviceSelection;
using static Session;
using static Workflow;

partial class Tests
{
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
        Put(Path.Combine(backup, ".gearbackup", "quarantine", "x", "projects", "user", "ghost.xy"), "QUARANTINED");
        File.Delete(Path.Combine(mirror, "projects", "user", "ziiamed 1.xy"));
        new BackupEngine().Run(mirror, history, backup, true, config,
            new OperationContext(new TestInteraction(), CancellationToken.None) { Device = xy });
        bool listed = false;
        foreach (string report in Directory.GetFiles(backup, "backup-statistics_*.txt"))
            listed |= File.ReadAllText(report).Contains("Projects no longer on the OP-XY (1):\r\n      ziiamed 1");
        Assert(listed, "a project removed from the OP-XY is reported");
        foreach (string report in Directory.GetFiles(backup, "backup-statistics_*.txt"))
            Assert(!File.ReadAllText(report).Contains("ghost"), "files in program metadata are never reported as removed projects");
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
        Assert(BackupDevice(backup) == camera && BackupDevice(Path.Combine(dir, "x100vi-test", "2026-01-01")) == null,
            "backup device identity requires its explicit metadata record");
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

    // Device/source pairs are independent; history names do not migrate or infer another device.
    static void TestProfileNames(string dir)
    {
        string source = Rooted(Path.Combine(dir, "named-source")); Directory.CreateDirectory(source);
        string key = SourceKey(source), profiles = Folder(dir, "named-histories");
        string old = Folder(profiles, key);
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.TrackerMini }.Activate())
        {
            Assert(key.StartsWith("folder-") && ProfileId(source, "") == "tracker-mini-" + key, "a history name starts with its device");
            Assert(ProfileFolder(profiles, source, "") == Path.Combine(profiles, "tracker-mini-" + key), "unprefixed histories are not reused");
            Assert(Directory.Exists(old), "history selection never renames an existing folder");
            Directory.CreateDirectory(Path.Combine(profiles, "tracker-mini-" + key));
            Assert(ProfileFolder(profiles, source, "studio") == Path.Combine(profiles, "studio"), "a named profile is used directly");
        }
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.M8 }.Activate())
            Assert(ProfileFolder(profiles, source, "") == Path.Combine(profiles, "m8-" + key), "another device has its own source history");
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

    // An MTP device going through the same detect -> prepare -> run steps as a real one, with a fake
    // device: its mirror is synced, backed up, and only downloads changes the next time.
    static void TestMtpSharedFlow(string dir)
    {
        var day = new DateTime(2026, 1, 1, 10, 0, 0);
        var device = new FakeMtpSource();
        device.File(@"synth\pad.aif", "PAD", day);
        device.File(@"tapes\track1.aif", "TAPE", day);
        int opened = 0;
        string mirrors = Folder(dir, "mirrors");
        var mtp = new MtpSource { Device = Devices.OP1Field, Mirror = Path.Combine(mirrors, "op1-field-TEST"),
            Info = new MtpDeviceInfo { InstanceId = @"USB\VID_2367&PID_0102\TEST", FriendlyName = "OP-1" },
            Open = () => { opened++; return device; } };
        Directory.CreateDirectory(mtp.Mirror);
        var global = new Config { BackupFolder = Folder(dir, "backups"), SettingsDirectory = dir, Preview = false };
        var choice = new SourceChoice { Path = mtp.Mirror, Detected = Devices.OP1Field, Mtp = mtp };
        PreparedSource prepared = null;
        OperationResult first = null, second = null;
        WithUi(() => {
            prepared = PrepareSource(global, choice);
            first = RunPrepared(prepared, false, false);
        });
        string backup = ListBackups(prepared.History)[0];
        Assert(first.ExitCode == 0 && opened == 1 && device.Downloads == 2 && prepared.Device == Devices.OP1Field &&
            File.ReadAllText(Path.Combine(backup, @"synth\pad.aif")) == "PAD", "a prepared MTP source is synced and backed up");
        device.File(@"tapes\track2.aif", "NEW TAPE", day);
        WithUi(() => second = RunPrepared(PrepareSource(global, choice), false, true));
        Assert(second.ExitCode == 0 && device.Downloads == 3 && File.ReadAllText(Path.Combine(backup, @"tapes\track2.aif")) == "NEW TAPE",
            "the next backup downloads only what changed");

        Throws(() => WithUi(() => RunPrepared(PrepareSource(global, choice), true, false)), "restoring onto an MTP device is refused");
        using (LockHistory(mirrors))
            Throws(() => WithUi(() => RunPrepared(PrepareSource(global, choice), false, true)), "the mirror isn't synced while storage cleanup holds its lock");
        Assert(opened == 2, "a refused run never opens the device");

        // The X100VI's mode is asked again right before syncing: in settings mode, photos aren't read at all
        // (a sync would see none and empty the mirror).
        int asked = 0;
        bool settingsMode = false;
        var camera = new MtpSource { Device = Devices.X100VI, Mirror = Path.Combine(mirrors, "x100vi-TEST"),
            ReadSettingsMode = () => { asked++; return settingsMode; },
            Info = new MtpDeviceInfo { InstanceId = @"USB\VID_04CB&PID_0305\TEST", FriendlyName = "X100VI" },
            Open = () => { opened++; return device; } };
        Directory.CreateDirectory(camera.Mirror);
        var cameraChoice = new SourceChoice { Path = camera.Mirror, Detected = Devices.X100VI, Mtp = camera, CameraSettingsMode = false };
        PreparedSource preparedCamera = null;
        WithUi(() => preparedCamera = PrepareSource(global, cameraChoice));
        settingsMode = true; // switched on the camera after detection, e.g. while a menu waited
        bool refused = false;
        WithUi(() => {
            try { RunPrepared(preparedCamera, false, false); }
            catch (IOException ex) { refused = ex.Message.Contains("settings mode"); }
        });
        Assert(refused && asked == 1 && opened == 2, "a camera switched to settings mode after detection is refused before its photos are read");
        Assert(Directory.Exists(camera.Mirror), "the mirror is left alone");
    }

    // Values a faulty MTP device could report.
    static void TestMtpDeviceValues()
    {
        Assert(OleDate(45000.5) == DateTime.FromOADate(45000.5), "a valid device date is read");
        Assert(OleDate(double.NaN) == DateTime.MinValue && OleDate(1e300) == DateTime.MinValue && OleDate(-1e300) == DateTime.MinValue,
            "an invalid device date counts as no date instead of failing the listing");
        Assert(MtpDevice.DownloadBufferSize(0) == 256 * 1024 && MtpDevice.DownloadBufferSize(1024 * 1024) == 1024 * 1024 &&
            MtpDevice.DownloadBufferSize(uint.MaxValue) == 4 * 1024 * 1024, "the device's buffer size is kept between 256 KB and 4 MB");

        // Listing a folder batch by batch, as a device sends it.
        int batches = 0;
        List<string> ids = MtpDevice.CollectIds(into => {
            for (int i = 0; i < (batches < 2 ? 100 : 40); i++) into.Add("id" + (batches * 100 + i));
            return ++batches < 3;
        });
        Assert(ids.Count == 240 && ids[0] == "id0" && ids[239] == "id239", "a folder's IDs are collected across several batches");
        batches = 0;
        ids = MtpDevice.CollectIds(into => {
            batches++;
            for (int i = 0; i < 100; i++) into.Add("same" + i);
            return true;
        });
        Assert(ids.Count == 100 && batches == 2, "a device repeating the same batch is stopped instead of looping forever");
        int counter = 0;
        Throws(() => MtpDevice.CollectIds(into => {
            for (int i = 0; i < 100; i++) into.Add("new" + counter++);
            return true;
        }, 5000), "a device that never stops listing new items is refused at the limit");
        Assert(counter <= 5100, "the endless listing stops right after the limit: " + counter + " IDs read");
        using (var cancel = new CancellationTokenSource())
        using (new OperationContext(new TestInteraction(), cancel.Token).Activate())
        {
            batches = 0;
            bool cancelled = false;
            try
            {
                MtpDevice.CollectIds(into => {
                    if (++batches == 3) cancel.Cancel();
                    for (int i = 0; i < 100; i++) into.Add("c" + batches + "-" + i);
                    return true;
                });
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && batches == 3, "Cancel stops a listing between batches");
        }
    }
}
