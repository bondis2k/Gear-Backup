// Safety and security: links in mirrors and backups, planted DLLs and temporary files,
// malformed manifests, and restores that fail part-way.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static FileCopy;
using static PathSafety;
using static Snapshots;
using static Backups;
using static SampleDependencies;
using static MtpMirror;
using static UsbDevices;
using static AtomicFile;
using static BackupStatuses;

partial class Tests
{
    [Test("Security: additional mirror link types", Folder = "helpers")]
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

    [Test("Security: file symbolic links in mirrors", Folder = "helpers")]
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

    [Test("Exact restore preserves extras after failure", Folder = "helpers")]
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
            result = new RestoreEngine().RunSelected(target, f.First, copies, RestoreMode.All, null, true,
                new Config { SingleContentStore = false, Preview = false, Verify = true, Thorough = true, Recovery = false }, new OperationContext(ui, CancellationToken.None));
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

    [Test("Malformed snapshot manifests", Folder = "helpers")]
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
        WithUi(() => RestorePointNames.SaveMetadata(backup, "input", DateTime.Now, false));
        Snapshot read = null;
        WithUi(() => read = ReadSnapshot(backup, manifest));
        Assert(read.Files.Count == 1 && read.Folders.Count == 1 && read.Files[0].Size == 4,
            "valid manifest remains accepted with a final newline and zero dates");
    }

    [Test("Security: MTP mirror links", Folder = "helpers")]
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

    [Test("Security: atomic text temporary files", Folder = "helpers")]
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

    [Test("Security hardening", Folder = "helpers")]
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
        new BackupEngine().Run(source, zipHistory, null, false, new Config { SingleContentStore = false, BackupFolder = dir, Preview = false, Zip = true },
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

    // Metadata reads and writes all check for links and size limits in one place (Records, AtomicText),
    // including writes that used to skip the check (checksum lists, status, snapshots, settings).
    [Test("Security: record reads and writes check links", Folder = "record-access")]
    static void TestRecordAccess(string root)
    {
        string elsewhere = Path.Combine(root, "elsewhere"), backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(backup);
        CreateTestJunction(AppIdentity.MetadataRoot(backup), elsewhere);
        WithUi(() => {
            Throws(() => SetBackupStatus(backup, Status.Complete), "a status write through a linked metadata folder is refused");
            Throws(() => AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "snapshots", "x.manifest"), "x"), "any record write through a link is refused");
        });
        Assert(Directory.GetFileSystemEntries(elsewhere).Length == 0, "nothing was written through the link");
        Put(Path.Combine(elsewhere, "status.txt"), "complete");
        Throws(() => BackupStatus(backup), "a status read through a linked metadata folder is refused");
        string plain = Path.Combine(root, "plain", "status.txt");
        Put(plain, new string('x', 5000));
        Throws(() => Records.ReadText(plain, Records.SmallLimit, "Backup status record"), "an oversized record is refused");
        Put(plain, "complete");
        Assert(Records.ReadText(plain, Records.SmallLimit, "Backup status record").Trim() == "complete", "an ordinary record reads normally");
    }
    [Test("Security: reserved Windows names")]
    static void TestReservedNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "gearbackup-reserved-names");
        foreach (string name in new[] { "CON", "nul.txt", "Songs\\aux.log", "COM1", "lpt9.x", "COM0", "CON .txt", "PRN.tar.gz", "CONIN$", "conout$.txt", "COM\u00b9", "Samples\\Nul\\kick.wav" })
            Throws(() => PathSafety.CheckedPath(root, name), "reserved Windows name is refused: " + name);
        foreach (string name in new[] { "console.txt", "com10.txt", "NULL.txt", "CONNECT.wav", "auxiliary", "Songs\\communication.m8s", "lpt.txt", "com.txt", "My Songs\\prn_01.m8s", "COMX.txt" })
            Assert(PathSafety.CheckedPath(root, name).StartsWith(PathSafety.Rooted(root), StringComparison.OrdinalIgnoreCase), "ordinary name is allowed: " + name);
        Assert(PathSafety.IsReservedName("Aux") && !PathSafety.IsReservedName("Auxx"), "reserved names are matched whole, ignoring case");
    }
}
