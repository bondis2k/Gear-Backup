// Backups: full, incremental, resume, history, verification, copying, zips, the command
// line and the engines behind both front ends.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

partial class GearBackup
{
    // ---- Tests ----

    static void TestSettingsAndProfiles(string root)
    {
        string source = Rooted(Path.Combine(root, "card"));
        var config = new Config { BackupFolder = root, Preview = false, Thorough = true, Verify = true };
        Assert(!new Config().Verify && !new Config().Thorough, "SHA-256 comparisons default off");
        Assert(!config.Recovery, "recovery defaults to no");
        Assert(RecoveryRoot(config) == Path.Combine(root, "Gear Recovery"), "empty recovery location defaults under backup folder");
        ApplySetting(config, "RecoveryFolder", "%USERPROFILE%\\M8-test-recovery");
        Assert(config.RecoveryFolder == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "M8-test-recovery"), "recovery folder expands environment variables");
        ApplySetting(config, "Recovery", "yes"); Assert(config.Recovery, "INI enables recovery");
        ApplySetting(config, "Recovery", "no"); Assert(!config.Recovery, "INI disables recovery");
        ApplySetting(config, "Thorough", "yes"); Assert(config.Thorough, "INI thorough mode");
        ApplySetting(config, "SourceFolder", source); Assert(Rooted(config.SourceFolder) == source, "manual source setting");
        Throws(() => ValidateProfile("../wrong"), "invalid profile rejected");
        Throws(() => ValidateProfile("CON"), "reserved profile rejected");
        Assert(ProfileId(source, "studio") == "studio", "explicit profile");
        Assert(ProfileId(source, "") != ProfileId(Path.Combine(root, "second-card"), ""), "folders have separate profiles");
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        using (var volume = new System.Management.ManagementObject("Win32_LogicalDisk.DeviceID='" + systemDrive.Substring(0, 2) + "'"))
        {
            volume.Get();
            Assert(ProfileId(systemDrive, "") == "m8-card-" + volume["VolumeSerialNumber"],
                "a drive's history is named after its device and serial number, the same as WMI reports it");
        }
        Throws(() => SafePath(root, "..\\escape.txt"), "traversal rejected");
        Throws(() => SafePath(root, "C:\\escape.txt"), "absolute manifest path rejected");
    }

    static void TestFullAndIncremental(string root)
    {
        string source = Rooted(Path.Combine(root, "card")), history = Path.Combine(root, "history");
        var config = new Config { BackupFolder = root, Preview = false, Thorough = true, Verify = true };
        string title, summary;
        Put(Path.Combine(source, "Songs", "one.m8s"), "AAAA");
        Put(Path.Combine(source, "Songs", "removed.m8s"), "REMOVED");
        Put(Path.Combine(source, "Songs", "dated_2026-01-01_120000.m8s"), "REAL DATED SONG");
        Put(Path.Combine(source, "Samples", "kick.wav"), "WAVE");
        Put(Path.Combine(source, "Bundles", "demo", "demo.m8s"), "DEMO");
        Put(Path.Combine(source, "Bundles", "demo", "kick.wav"), "WAVE");
        Put(Path.Combine(source, "Thumbs.db"), "ignored");
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        PreviewOnly = true;
        Assert(Backup(source, history, null, false, config, out title, out summary) == 0, "backup preview succeeds");
        Assert(!Directory.Exists(history), "preview creates no backup files");
        PreviewOnly = false;
        Assert(Backup(source, history, null, false, config, out title, out summary) == 0, "full backup succeeds");
        string backup = ListBackups(history)[0];
        Assert(BackupStatus(backup) == Status.Complete, "full backup complete status");
        Snapshot first = LatestSnapshot(backup);
        Assert(first.Files.Count == 6, "snapshot contains exactly user files");
        Assert(first.Folders.Contains("Empty"), "empty folder recorded");
        Assert(!File.Exists(Path.Combine(backup, "Thumbs.db")), "OS junk excluded");
        Assert(LoadChecksums(backup).Count == 6, "metadata not included in checksums");
        int objects = Directory.GetFiles(Path.Combine(backup, ".gearbackup", "objects")).Length;
        Assert(objects == 5, "identical samples share one history object");

        string song = Path.Combine(source, "Songs", "one.m8s");
        DateTime oldTime = File.GetLastWriteTimeUtc(song);
        Put(song, "BBBB"); File.SetLastWriteTimeUtc(song, oldTime);
        Assert(CompareContent(new FileInfo(song), Path.Combine(backup, "Songs", "one.m8s"), false) == FileState.Same, "fast comparison preserves original behavior");
        Assert(CompareContent(new FileInfo(song), Path.Combine(backup, "Songs", "one.m8s"), true) == FileState.Changed, "thorough detects same-size same-time changes");
        File.Delete(Path.Combine(source, "Songs", "removed.m8s"));
        Assert(Backup(source, history, backup, true, config, out title, out summary) == 0, "incremental succeeds");
        Snapshot second = LatestSnapshot(backup);
        Assert(second.Files.Count == 5, "deleted file absent from current snapshot");
        Assert(File.Exists(Path.Combine(backup, "Songs", "removed.m8s")), "incremental retains deleted file in browsable backup");
        Assert(File.ReadAllText(first.Files.Find(f => f.Relative == "Songs\\one.m8s").StoredPath) == "AAAA", "old snapshot contents immutable");
        Assert(second.Files.Exists(f => f.Relative == "Songs\\dated_2026-01-01_120000.m8s"), "genuine dated song included in snapshot");

        string namingHistory = Path.Combine(root, "same-day-fulls");
        Assert(Backup(source, namingHistory, null, false, config, out title, out summary) == 0, "first same-day full backup");
        string firstFull = ListBackups(namingHistory)[0];
        Assert(Backup(source, namingHistory, firstFull, false, config, out title, out summary) == 0, "second same-day full backup");
        var sameDay = ListBackups(namingHistory);
        Assert(sameDay.Count == 2 && sameDay[0] == firstFull && sameDay[1] != firstFull, "new full backup names sort chronologically without collision");
    }

    static void TestResume(string root)
    {
        Fixture f = BackedUpCard(root, true);
        string title, summary;
        DateTime oldTime = File.GetLastWriteTimeUtc(f.Song);
        SetBackupStatus(f.Backup, Status.Incomplete);
        Put(Path.Combine(f.Backup, "Songs", "one.m8s"), "PARTIAL");
        Assert(Backup(f.Source, f.History, f.Backup, true, f.Config, out title, out summary) == 0, "resume repairs partial file despite fast setting");
        Assert(File.ReadAllText(Path.Combine(f.Backup, "Songs", "one.m8s")) == "BBBB", "resume restores source content");
        Assert(BackupStatus(f.Backup) == Status.Complete, "resume complete status");
        Assert(Directory.GetFiles(Path.Combine(f.Backup, ".gearbackup", "quarantine"), "*", SearchOption.AllDirectories).Length == 1, "partial copy quarantined");
        int checkedOk = 0, checkedDamaged = 0, checkedMissing = 0, uncheckedBackups = 0;
        CheckBackup(f.Backup, ref checkedOk, ref checkedDamaged, ref checkedMissing, ref uncheckedBackups);
        Assert(checkedDamaged == 0 && checkedMissing == 0, "resume does not bless partial files as valid versions");
        // Simulate an interrupted incremental after the file was copied but before checksums were updated.
        SetBackupStatus(f.Backup, Status.Incomplete);
        Put(f.Song, "CCCC"); File.SetLastWriteTimeUtc(f.Song, oldTime);
        Put(Path.Combine(f.Backup, "Songs", "one.m8s"), "CCCC");
        Assert(Backup(f.Source, f.History, f.Backup, true, f.Config, out title, out summary) == 0, "resume refreshes stale checksum entries");
        Assert(LoadChecksums(f.Backup)["Songs\\one.m8s"] == HashFile(f.Song), "resumed checksum matches repaired file");
    }

    static void TestCommandLine(string root)
    {
        string source = Rooted(Path.Combine(root, "card"));
        Put(Path.Combine(source, "Songs", "one.m8s"), "AAAA");
        Put(Path.Combine(source, "Samples", "kick.wav"), "WAVE");
        // Run the real argument/source/profile entry flow against a private INI beside the test executable.
        string testIni = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GearBackup.ini");
        Put(testIni, "BackupFolder=" + root + "\nMode=full\nPreview=no\nRecovery=no\nThorough=yes\n");
        Assert(Run(false, false, new[] { "--source", source, "--profile", "card-A" }) == 0, "CLI manual source full backup");
        Assert(ListBackups(Path.Combine(root, "Gear Backups", "profiles", "card-A")).Count == 1, "CLI named profile uses separate history");
        Assert(Run(false, false, new[] { "--source", source, "--profile", "card-B" }) == 0, "second CLI profile backup");
        Assert(ListBackups(Path.Combine(root, "Gear Backups", "profiles", "card-B")).Count == 1, "second profile separated");
        string iniBeforePreview = File.ReadAllText(testIni);
        PreviewOnly = true;
        Assert(Run(false, false, new[] { "--source", source, "--profile", "preview-card", "--preview" }) == 0, "CLI preview entry flow");
        Assert(!Directory.Exists(Path.Combine(root, "Gear Backups", "profiles", "preview-card")), "CLI preview creates no history or lock");
        Assert(File.ReadAllText(testIni) == iniBeforePreview, "preview does not update INI");
        PreviewOnly = false;
        Console.SetIn(new StringReader("1\n"));
        Assert(Run(false, true, new[] { "--check", "--profile", "card-A" }) == 0, "CLI checks profile without source/device");
        Throws(() => Run(false, false, new[] { "--source" }), "CLI missing argument rejected");
        Throws(() => Run(false, false, new[] { "--invalid" }), "CLI unknown argument rejected");
        Throws(() => Run(false, false, new[] { "--source", root }), "overlapping source and backup rejected");
    }

    static void TestHistoryIntegrity(string root)
    {
        Fixture f = BackedUpCard(root, true);
        string title, summary;
        var one = new List<RestoreEntry> { f.First.Files.Find(e => e.Relative == "Songs\\one.m8s") };
        string malicious = Path.Combine(root, "bad.manifest");
        Put(malicious, "GEARSNAPSHOT\t1\nD\t" + Encode("..\\outside") + "\n");
        Throws(() => ReadSnapshot(f.Backup, malicious), "malicious manifest rejected");
        int ok = 0, damaged = 0, missing = 0;
        CheckSnapshots(f.Backup, ref ok, ref damaged, ref missing);
        Assert(damaged == 0 && missing == 0 && ok > 0, "history check succeeds");
        Put(one[0].StoredPath, "CORRUPTED OBJECT");
        Throws(() => ValidateEntry(one[0]), "damaged history refused before restore");
        CheckSnapshots(f.Backup, ref ok, ref damaged, ref missing);
        Assert(damaged > 0, "history check detects damage");
        // Corrupted object belongs to old snapshot; current-source objects can still be valid.
        // A selected current object corruption must fail snapshot creation and mark backup failed.
        RestoreEntry currentSong = LatestSnapshot(f.Backup).Files.Find(e => e.Relative == "Songs\\one.m8s");
        Put(currentSong.StoredPath, "BAD");
        Assert(Backup(f.Source, f.History, f.Backup, true, f.Config, out title, out summary) == 2, "history failure prevents successful backup");
        Assert(BackupStatus(f.Backup) == Status.Failed, "history failure sets failed status");
    }

    static void TestVerifyOff(string root)
    {
        Fixture f = BackedUpCard(root, true);
        string title, summary;
        // Opt-out tests: preserve hashing records without comparing SHA-256 automatically.
        string optionalPath = Path.Combine(root, "optional-check.bin");
        Put(optionalPath, "AAAA");
        var optionalEntry = new RestoreEntry { Relative = "optional.bin", StoredPath = optionalPath, Hash = HashFile(optionalPath),
            Size = 4, ModifiedTicks = File.GetLastWriteTimeUtc(optionalPath).Ticks, CreatedTicks = File.GetCreationTimeUtc(optionalPath).Ticks };
        Put(optionalPath, "BBBB");
        ValidateEntry(optionalEntry, false);
        Assert(true, "Verify=no skips restore-source checksum comparison");
        Throws(() => ValidateEntry(optionalEntry, true), "Verify=yes detects same-size source corruption");
        string optionalTarget = Rooted(Path.Combine(root, "optional-target"));
        Put(Path.Combine(optionalTarget, "optional.bin"), "CCCC");
        File.SetLastWriteTimeUtc(Path.Combine(optionalTarget, "optional.bin"), new DateTime(optionalEntry.ModifiedTicks, DateTimeKind.Utc));
        Assert(EntryMatchesTarget(optionalEntry, Path.Combine(optionalTarget, "optional.bin"), false), "Thorough=no uses metadata instead of content");
        Assert(!EntryMatchesTarget(optionalEntry, Path.Combine(optionalTarget, "optional.bin"), true), "Thorough=yes compares content");
        Assert(Fingerprint(Path.Combine(optionalTarget, "optional.bin"), false).StartsWith("meta:"), "default guard records metadata fingerprint");
        var optionalCopies = new List<RestoreEntry> { optionalEntry };
        var optionalHashes = new Dictionary<string, string> { { "optional.bin", Fingerprint(Path.Combine(optionalTarget, "optional.bin"), false) } };
        var offConfig = new Config { BackupFolder = root, Preview = false };
        ExecuteRestore(optionalTarget, optionalCopies, new List<string>(), new List<string>(), new List<string>(), optionalHashes, offConfig, out title, out summary);
        Assert(title == "Gear Backup restore complete" && File.ReadAllText(Path.Combine(optionalTarget, "optional.bin")) == "BBBB", "Verify=no permits restore without stored-checksum comparison");
        Assert(CopyFile(new FileInfo(optionalPath), Path.Combine(root, "optional-copy.bin"), false) == HashFile(optionalPath), "checksum records are generated when verification is off");
        f.Config.Verify = false;
        RestoreEntry currentSong = f.Latest.Files.Find(e => e.Relative == "Songs\\one.m8s");
        Put(currentSong.StoredPath, "DDDD"); // Same size as the source; only its checksum differs.
        Assert(Backup(f.Source, f.History, f.Backup, true, f.Config, out title, out summary) == 0, "Verify=no skips snapshot object checksum comparison");
        int optionalOk = 0, optionalDamaged = 0, optionalMissing = 0;
        CheckSnapshots(f.Backup, ref optionalOk, ref optionalDamaged, ref optionalMissing);
        Assert(optionalDamaged > 0, "explicit integrity check still detects corruption with defaults off");
    }

    static void TestUndatedFiles(string root)
    {
        string title, summary;
        // Reproduce undated M8 files without enabling SHA-256 comparison.
        Assert(!UsableFileTime(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)), "FILETIME-zero timestamp is unusable");
        string undatedSource = Rooted(Path.Combine(root, "undated-card"));
        string undatedSong = Path.Combine(undatedSource, "Songs", "undated.m8s");
        Put(undatedSong, "UNCHANGED");
        File.SetLastWriteTimeUtc(undatedSong, new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        string undatedHistory = Path.Combine(root, "undated-history");
        var fastConfig = new Config { BackupFolder = root, Preview = false };
        Assert(Backup(undatedSource, undatedHistory, null, false, fastConfig, out title, out summary) == 0, "undated initial backup succeeds with SHA off");
        string undatedBackup = ListBackups(undatedHistory)[0];
        string undatedCopy = Path.Combine(undatedBackup, "Songs", "undated.m8s");
        Assert(UsableFileTime(File.GetLastWriteTimeUtc(undatedCopy)), "undated copy retains destination timestamp");
        Assert(CompareContent(new FileInfo(undatedSong), undatedCopy, false) == FileState.Same, "different timestamps do not mark identical undated contents changed");
        TextWriter previousOutput = Console.Out;
        var captured = new StringWriter();
        PreviewOnly = true;
        try { Console.SetOut(captured); Backup(undatedSource, undatedHistory, undatedBackup, true, fastConfig, out title, out summary); }
        finally { Console.SetOut(previousOutput); PreviewOnly = false; }
        Assert(captured.ToString().Contains("0 action(s)") && !captured.ToString().Contains("Changed: Songs"), "incremental preview excludes unchanged undated files");
        fastConfig.Preview = true;
        Console.SetIn(new StringReader("")); // Any unexpected acceptance prompt must fail rather than block.
        var noChangesOutput = new StringWriter();
        int noChangesResult;
        try { Console.SetOut(noChangesOutput); noChangesResult = Backup(undatedSource, undatedHistory, undatedBackup, true, fastConfig, out title, out summary); }
        finally { Console.SetOut(previousOutput); fastConfig.Preview = false; }
        Assert(noChangesResult == 0, "unchanged incremental succeeds without confirmation even with Preview=yes");
        Assert(!noChangesOutput.ToString().Contains("Proceed?") && noChangesOutput.ToString().Contains("No new or changed files"), "no-change run reports result without acceptance prompt");
        Assert(Directory.GetFiles(Path.Combine(undatedBackup, "Songs"), "*.m8s").Length == 1, "no spurious old versions for undated files");
        var missingDateEntry = new RestoreEntry { Relative = "Songs\\undated.m8s", StoredPath = undatedCopy, Size = 9,
            ModifiedTicks = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks };
        Assert(EntryMatchesTarget(missingDateEntry, undatedSong, false), "restore skips identical FILETIME-zero snapshot contents");
        Put(undatedSong, "EDITED!!!");
        File.SetLastWriteTimeUtc(undatedSong, new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert(CompareContent(new FileInfo(undatedSong), undatedCopy, false) == FileState.Changed, "same-size undated edit detected without SHA-256");
        Assert(!EntryMatchesTarget(missingDateEntry, undatedSong, false), "restore detects differing undated contents");
    }

    static void TestSmallHelpers(string root)
    {
        string blockA = Path.Combine(root, "blocks-a.bin"), blockB = Path.Combine(root, "blocks-b.bin");
        byte[] blockBytes = new byte[131077];
        File.WriteAllBytes(blockA, blockBytes); File.WriteAllBytes(blockB, blockBytes);
        Assert(FilesEqual(blockA, blockB), "byte comparison spans multiple buffers");
        blockBytes[131076] = 1; File.WriteAllBytes(blockB, blockBytes);
        Assert(!FilesEqual(blockA, blockB), "byte comparison finds difference in final partial buffer");
        Assert(DriveSpaceText(@"\\server\share") == "unknown", "statistics free-space text tolerates network sources");
        Assert(DriveSpaceText(Path.GetPathRoot(root)).Contains(" of "), "statistics free-space text shows free and total size");
        var original = new Config();
        foreach (var field in typeof(Config).GetFields())
        {
            if (field.FieldType == typeof(bool)) field.SetValue(original, !(bool)field.GetValue(original));
            else if (field.FieldType == typeof(int)) field.SetValue(original, 7);
            else if (field.FieldType == typeof(string)) field.SetValue(original, "x");
        }
        Config cloned = original.Clone();
        Assert(Array.TrueForAll(typeof(Config).GetFields(), field => Equals(field.GetValue(cloned), field.GetValue(original))),
            "settings copy keeps every setting, including ones the GUI window doesn't show");
    }

    static void TestHistoryLock(string root)
    {
        string history = Path.Combine(root, "history");
        using (LockHistory(history)) Throws(() => LockHistory(history), "concurrent history writer rejected");
        using (LockHistory(history)) { }
        Assert(true, "the lock is released when disposed");
    }

    static void TestZip(string root)
    {
        string source = Rooted(Path.Combine(root, "zip-source"));
        Put(Path.Combine(source, "Songs", "a.m8s"), "ZIP SONG");
        Put(Path.Combine(source, "Samples", "kick.wav"), "ZIP KICK");
        string undated = Path.Combine(source, "Songs", "undated.m8s");
        Put(undated, "OLD");
        File.SetLastWriteTimeUtc(undated, new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(source, "Empty"));
        string history = Path.Combine(root, "zip-history");
        var config = new Config { BackupFolder = root, Preview = false, Zip = true };

        var ui = new TestInteraction();
        OperationResult full = new BackupEngine().Run(source, history, null, false, config, new OperationContext(ui, CancellationToken.None));
        string backup = ListBackups(history)[0], zipPath = backup + ".zip";
        Assert(full.ExitCode == 0 && File.Exists(zipPath) && full.Summary.Contains(Path.GetFileName(zipPath)),
            "full backup with --zip saves a zip next to the backup folder");
        Assert(!File.Exists(zipPath + ".tmp"), "no temporary zip left behind");
        using (var zip = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
        {
            var names = new HashSet<string>();
            foreach (ZipArchiveEntry entry in zip.Entries) names.Add(entry.FullName);
            Assert(names.Contains("Songs/a.m8s") && names.Contains("Samples/kick.wav") && names.Contains("Empty/") &&
                names.Contains("checksums.sha256") && names.Contains("backup-statistics.txt"),
                "zip holds the card's files, empty folders, checksums and statistics");
            Assert(!new List<string>(names).Exists(n => n.StartsWith(".gearbackup")), "zip leaves out the history store");
            using (var reader = new StreamReader(zip.GetEntry("Songs/a.m8s").Open()))
                Assert(reader.ReadToEnd() == "ZIP SONG", "zipped content matches the backup");
            Assert(zip.GetEntry("Songs/undated.m8s") != null, "undated files are zipped");
            Assert(ZipTime(new DateTime(1601, 1, 2)).Year == 1980 && ZipTime(new DateTime(2026, 10, 10)).Year == 2026,
                "dates a zip can't store become 1 January 1980; others are kept");
        }

        ui = new TestInteraction();
        OperationResult incremental = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(ui, CancellationToken.None));
        Assert(incremental.ExitCode == 0 && Directory.GetFiles(history, "*.zip").Length == 1 &&
            ui.Output.ToString().Contains("applies to full backups"), "--zip is skipped, with a note, for incremental backups");
    }

    static void TestCopyPipeline(string root)
    {
        var random = new Random(8);
        foreach (int size in new[] { 0, 1, ChunkSize - 1, ChunkSize, 3 * ChunkSize + 12345 })
        {
            byte[] data = new byte[size];
            random.NextBytes(data);
            string from = Path.Combine(root, "pipe-" + size + ".bin"), to = from + ".copy";
            File.WriteAllBytes(from, data);
            var ui = new TestInteraction();
            string hash;
            using (new OperationContext(ui, CancellationToken.None).Activate()) hash = CopyFile(new FileInfo(from), to, true);
            Assert(hash == HashFile(from) && FilesEqual(from, to), "pipelined copy is exact for " + size + " bytes");
            Assert(ui.Progress.Exists(p => p.Stage == Stage.Verifying) || size == 0, "verification reads the copy back from disk, bypassing the cache");
            Assert(HashFileFromDisk(to) == hash, "unbuffered read-back gives the same checksum for " + size + " bytes");
        }

        Throws(() => CopyAndHash(new FailingStream(ChunkSize + 10), new MemoryStream(), "failing"),
            "a read error during a pipelined copy surfaces as an IOException");

        string big = Path.Combine(root, "pipe-" + (3 * ChunkSize + 12345) + ".bin"), cancelledCopy = big + ".cancelled";
        var cts = new CancellationTokenSource();
        var cancelUi = new TestInteraction();
        cancelUi.OnProgress = delegate(OperationProgress p) { if (p.Stage == Stage.Copying) cts.Cancel(); };
        bool cancelled = false;
        using (new OperationContext(cancelUi, cts.Token).Activate())
        {
            try { CopyFile(new FileInfo(big), cancelledCopy, false); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        Assert(cancelled && !File.Exists(cancelledCopy), "cancelling mid-copy stops and leaves no partial file");
    }

    static void TestWithoutHistory(string root)
    {
        Assert(new Config().PointInTimeHistory, "point-in-time history is enabled by default");
        string source = Rooted(Path.Combine(root, "source"));
        Put(Path.Combine(source, "song.txt"), "FIRST");
        Put(Path.Combine(source, "unchanged.txt"), "SAME");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        string history = Path.Combine(root, "history");
        var config = new Config { PointInTimeHistory = false, HardLinks = true, Verify = true, Thorough = true, Preview = false };
        var ui = new TestInteraction();
        OperationResult result = new BackupEngine().Run(source, history, null, false, config, new OperationContext(ui, CancellationToken.None));
        string backup = ListBackups(history)[0];
        Assert(result.ExitCode == 0 && BackupStatus(backup) == Status.Complete, "full backup completes with history disabled");
        Assert(!Directory.Exists(SnapshotRoot(backup)) && !Directory.Exists(Path.Combine(backup, ".gearbackup", "objects")), "no snapshot or second content store is created");
        Assert(ui.Output.ToString().Contains("no extra history content store"), "space planning ignores history even with HardLinks enabled");
        Assert(LoadChecksums(backup).Count == 2, "history-free backup still has checksums");

        Put(Path.Combine(source, "song.txt"), "SECOND VERSION");
        Put(Path.Combine(source, "new.txt"), "NEW");
        result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && result.Counts.Copied == 2 && result.Counts.Unchanged == 1, "incremental backup updates changed and new files and skips unchanged files");
        Assert(BackupFiles(backup).Exists(f => File.ReadAllText(f) == "FIRST"), "old browsable versions remain independent of snapshot history");
        int ok = 0, damaged = 0, missing = 0, uncheckedFiles = 0;
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        {
            CheckBackup(backup, ref ok, ref damaged, ref missing, ref uncheckedFiles);
            CheckSnapshots(backup, ref ok, ref damaged, ref missing);
        }
        Assert(ok == 4 && damaged == 0 && missing == 0 && uncheckedFiles == 0, "integrity checks verify current files and retained versions without snapshots");
        Snapshot current = CurrentBackup(backup);
        string target = Rooted(Path.Combine(root, "restored"));
        Directory.CreateDirectory(target);
        result = new RestoreEngine().RunSelected(target, current, current.Files, 'a', null, false, config,
            new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "song.txt")) == "SECOND VERSION" &&
            File.ReadAllText(Path.Combine(target, "new.txt")) == "NEW" && Directory.Exists(Path.Combine(target, "empty")),
            "current files and empty folders can be restored with history disabled");
        Assert(!Directory.Exists(SnapshotRoot(backup)), "incremental backups also omit snapshots");

        string resumeSource = Rooted(Path.Combine(root, "resume-source"));
        Directory.CreateDirectory(resumeSource);
        File.WriteAllBytes(Path.Combine(resumeSource, "large.wav"), new byte[2 * 1024 * 1024]);
        string resumeHistory = Path.Combine(root, "resume-history");
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.Copying) cancellation.Cancel(); };
            result = new BackupEngine().Run(resumeSource, resumeHistory, null, false, config, new OperationContext(ui, cancellation.Token));
            Assert(result.Cancelled && BackupStatus(ListBackups(resumeHistory)[0]) == Status.Incomplete,
                "cancelled history-free backup remains resumable");
        }
        string interrupted = ListBackups(resumeHistory)[0];
        result = new BackupEngine().Run(resumeSource, resumeHistory, interrupted, true, config, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && BackupStatus(interrupted) == Status.Complete &&
            LoadChecksums(interrupted)["large.wav"] == HashFile(Path.Combine(resumeSource, "large.wav")) && !Directory.Exists(SnapshotRoot(interrupted)),
            "resuming without history completes copies and checksum records without creating snapshots");

        config.PointInTimeHistory = true;
        result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && LatestSnapshot(backup).Files.Count == 3, "enabling history later captures the current source membership");
        int manifests = Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length;
        config.PointInTimeHistory = false;
        result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(new TestInteraction(), CancellationToken.None));
        Assert(result.ExitCode == 0 && Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == manifests,
            "disabling history preserves existing snapshots without adding one");
    }

    static void TestOptimizations(string root)
    {
        // History objects are hard links to the backup files on NTFS: one copy of the content on disk.
        string source = Rooted(Path.Combine(root, "opt-source"));
        string song = Path.Combine(source, "Songs", "same-size.m8s");
        Put(song, "AAAA");
        DateTime songTime = File.GetLastWriteTimeUtc(song);
        string history = Path.Combine(root, "opt-history");
        Assert(!new Config().HardLinks, "hard-linked history is opt-in");
        var config = new Config { BackupFolder = root, Preview = false, Thorough = true, HardLinks = true };
        var ui = new TestInteraction();
        OperationResult first = new BackupEngine().Run(source, history, null, false, config, new OperationContext(ui, CancellationToken.None));
        Assert(first.ExitCode == 0, "optimization fixture backup succeeds");
        string backup = ListBackups(history)[0];
        string treeFile = Path.Combine(backup, "Songs", "same-size.m8s");
        string objectFile = LatestSnapshot(backup).Files[0].StoredPath;
        if (SupportsHardLinks(root))
        {
            Assert(ui.Output.ToString().Contains("no extra space needed"), "NTFS history needs no extra space");
            File.AppendAllText(Path.Combine(root, "opt-probe.txt"), "");
            string probe = Path.Combine(root, "opt-probe.txt"), probeLink = Path.Combine(root, "opt-probe-link.txt");
            Assert(TryHardLink(probe, probeLink), "hard link created");
            File.WriteAllText(probe, "SHARED");
            Assert(File.ReadAllText(probeLink) == "SHARED", "hard link shares content");
            Assert(File.ReadAllText(objectFile) == "AAAA" && File.ReadAllText(treeFile) == "AAAA", "history object holds backed-up content");
            File.WriteAllText(treeFile, "AAAZ");
            Assert(File.ReadAllText(objectFile) == "AAAZ", "HardLinks=yes shares one copy between backup file and history");
            File.WriteAllText(treeFile, "AAAA");

            // A check reads hard-linked content once: the history object isn't hashed a second time.
            var checkUi = new TestInteraction();
            var verifiedLinks = new Dictionary<string, string>();
            int linkOk = 0, linkDamaged = 0, linkMissing = 0, linkUnchecked = 0;
            using (new OperationContext(checkUi, CancellationToken.None).Activate())
            {
                CheckBackup(backup, ref linkOk, ref linkDamaged, ref linkMissing, ref linkUnchecked, verifiedLinks);
                CheckSnapshots(backup, ref linkOk, ref linkDamaged, ref linkMissing, verifiedLinks);
            }
            Assert(linkDamaged == 0 && linkMissing == 0 && linkOk == 2, "hard-linked backup checks OK");
            Assert(!checkUi.Progress.Exists(p => p.Stage == Stage.Hashing && p.Path == objectFile), "check skips re-reading hard-linked history");
            File.WriteAllText(treeFile, "AAAX");
            linkOk = 0; verifiedLinks.Clear();
            using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            {
                CheckBackup(backup, ref linkOk, ref linkDamaged, ref linkMissing, ref linkUnchecked, verifiedLinks);
                CheckSnapshots(backup, ref linkOk, ref linkDamaged, ref linkMissing, verifiedLinks);
            }
            Assert(linkDamaged == 2, "damage to hard-linked content is reported for the backup file and its history");
            File.WriteAllText(treeFile, "AAAA");
        }

        // Full backup with Thorough=yes: no hashing before copying, and a same-size, same-time
        // content edit is still reported, using the checksums from copying.
        Put(song, "BBBB");
        File.SetLastWriteTimeUtc(song, songTime);
        ui = new TestInteraction();
        var hashedBeforeCopy = new List<string>();
        bool copying = false;
        ui.OnProgress = delegate(OperationProgress p) {
            if (p.Stage == Stage.Copying) copying = true;
            if (!copying && p.Stage == Stage.Hashing) hashedBeforeCopy.Add(p.Path);
        };
        OperationResult second = new BackupEngine().Run(source, history, backup, false, config, new OperationContext(ui, CancellationToken.None));
        Assert(second.ExitCode == 0 && hashedBeforeCopy.Count == 0, "thorough full backup reads the card once");
        Assert(second.Summary.Contains("1 edited (same-size)"), "thorough full backup reports content-only edits");

        // Link checks are remembered only within one operation: a folder replaced by a junction
        // later is rejected by the next operation.
        string linkRoot = Path.Combine(root, "opt-links");
        Directory.CreateDirectory(Path.Combine(linkRoot, "sub"));
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            SafePath(linkRoot, "sub\\file.txt");
        Directory.Delete(Path.Combine(linkRoot, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "opt-elsewhere"));
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c mklink /J \"" + Path.Combine(linkRoot, "sub") + "\" \"" + Path.Combine(root, "opt-elsewhere") + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        mklink.WaitForExit();
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            Throws(() => SafePath(linkRoot, "sub\\file.txt"), "junction created after an earlier operation is rejected");
        Directory.Delete(Path.Combine(linkRoot, "sub"));  // removes only the junction
    }

    static void TestEngineOperations(string root)
    {
        string source = Rooted(Path.Combine(root, "engine-source"));
        Put(Path.Combine(source, "Songs", "one.m8s"), "HEADLESS");
        Put(Path.Combine(source, "Songs", "two.m8s"), "HEADLESS");
        string history = Path.Combine(root, "engine-history");
        var config = new Config { BackupFolder = root, Preview = true };
        var ui = new TestInteraction();
        bool delayed = false;
        ui.OnProgress = delegate(OperationProgress p) {
            if (!delayed && p.Stage == Stage.SavingHistory) { delayed = true; Thread.Sleep(350); }
        };
        var context = new OperationContext(ui, CancellationToken.None);
        OperationResult result = new BackupEngine().Run(source, history, null, false, config, context);
        Assert(result.ExitCode == 0 && !result.Cancelled && ui.Plans == 1, "backup engine uses injected plan review without console input");
        Assert(ui.Progress.Exists(p => p.Stage == Stage.Scanning) && ui.Progress.Exists(p => p.Stage == Stage.Copying) &&
            ui.Progress.Exists(p => p.Stage == Stage.SavingHistory), "structured backup progress covers scan, copy, and history");
        Assert(!ui.Progress.Exists(p => p.Stage == Stage.Hashing), "default full backup avoids standalone preflight hashing reads");
        Assert(result.Elapsed.TotalMilliseconds >= 350, "engine elapsed time includes snapshot creation");
        var time = System.Text.RegularExpressions.Regex.Match(ui.Output.ToString(), @"Time:\s+([\d.,]+)s");
        Assert(time.Success && double.Parse(time.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) >= 0.3,
            "printed backup duration includes snapshot creation");
        string backup = ListBackups(history)[0];
        Snapshot snapshot = LatestSnapshot(backup);
        Put(Path.Combine(backup, ".gearbackup", "objects", "not-a-user-file"), "HIDDEN");
        Assert(BackupFiles(backup).Count == 2, "backup traversal excludes metadata and statistics");
        Assert(CurrentBackup(backup).Files.Count == 2, "current restore view excludes history store contents");

        ui = new TestInteraction();
        config.Thorough = true;
        config.Verify = true;
        result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(ui, CancellationToken.None));
        string sharedObject = snapshot.Files[0].StoredPath;
        Assert(ui.Progress.FindAll(p => p.Stage == Stage.Hashing && p.Path == sharedObject && p.Bytes == p.TotalBytes).Count == 1,
            "shared snapshot object verified only once per run");
        Assert(ui.Progress.FindAll(p => p.Stage == Stage.Hashing && p.Path == Path.Combine(source, "Songs", "one.m8s") && p.Bytes == p.TotalBytes).Count == 1,
            "thorough planning reuses source hash instead of rehashing for space estimate");
        Assert(result.ExitCode == 0 && ui.Plans == 0, "unchanged incremental needs no injected confirmation");

        string target = Rooted(Path.Combine(root, "engine-target"));
        Directory.CreateDirectory(target);
        ui = new TestInteraction();
        result = new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, 'a', null, false,
            config, new OperationContext(ui, CancellationToken.None));
        Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "HEADLESS",
            "restore planning and execution run through injected frontend");
        Assert(ui.Plans == 1 && ui.Progress.Exists(p => p.Stage == Stage.PlanningRestore), "restore engine reports planning and reviews plan");

        string cancelSource = Rooted(Path.Combine(root, "cancel-source"));
        FileInfo large = new FileInfo(Path.Combine(cancelSource, "large.wav"));
        Directory.CreateDirectory(cancelSource);
        File.WriteAllBytes(large.FullName, new byte[2 * 1024 * 1024]);
        string cancelHistory = Path.Combine(root, "cancel-history");
        config = new Config { BackupFolder = root, Preview = false };
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.Copying) cancellation.Cancel(); };
            result = new BackupEngine().Run(cancelSource, cancelHistory, null, false, config, new OperationContext(ui, cancellation.Token));
            string interrupted = ListBackups(cancelHistory)[0];
            Assert(result.Cancelled && result.ExitCode == 0 && BackupStatus(interrupted) == Status.Incomplete, "cancelled backup remains resumable");
            Assert(!File.Exists(Path.Combine(interrupted, "large.wav")), "cancelling during copy removes partial backup file");
        }

        // Cancelling a changed incremental copy restores its previous browsable version.
        Put(Path.Combine(source, "Songs", "one.m8s"), "CHANGED CONTENT");
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.Copying) cancellation.Cancel(); };
            result = new BackupEngine().Run(source, history, backup, true, config, new OperationContext(ui, cancellation.Token));
            Assert(result.Cancelled && File.ReadAllText(Path.Combine(backup, "Songs", "one.m8s")) == "HEADLESS",
                "cancelled incremental replacement puts old backup copy back");
            Assert(Directory.GetFiles(SnapshotRoot(backup), "*.manifest").Length == 2, "cancelled backup does not publish a snapshot");
        }

        string cancelTarget = Rooted(Path.Combine(root, "cancel-target"));
        Put(Path.Combine(cancelTarget, "Songs", "one.m8s"), "KEEP ORIGINAL");
        Put(Path.Combine(cancelTarget, "extra.wav"), "KEEP EXTRA");
        var copies = new List<RestoreEntry> { snapshot.Files.Find(f => f.Relative == "Songs\\one.m8s") };
        var removals = new List<string> { "extra.wav" };
        var fingerprints = TargetHashes(cancelTarget, copies, removals);
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.Copying) cancellation.Cancel(); };
            result = new RestoreEngine().Run(cancelTarget, copies, removals, new List<string>(), new List<string>(), fingerprints,
                config, new OperationContext(ui, cancellation.Token));
            Assert(result.Cancelled && File.ReadAllText(Path.Combine(cancelTarget, "Songs", "one.m8s")) == "KEEP ORIGINAL",
                "cancelled staged restore preserves existing target");
            Assert(File.Exists(Path.Combine(cancelTarget, "extra.wav")), "cancelled restore does not remove extras");
            Assert(Directory.GetFiles(cancelTarget, ".gearrestore-*.tmp", SearchOption.AllDirectories).Length == 0,
                "cancelled restore cleans staged file");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.Hashing) cancellation.Cancel(); };
            result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(ui, cancellation.Token));
            Assert(result.Cancelled && result.Title == "Gear Backup check cancelled", "integrity cancellation is not reported as corruption or success");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.Copying) cancellation.Cancel(); };
            var recoveryConfig = new Config { BackupFolder = root, Recovery = true, RecoveryFolder = Path.Combine(root, "cancel-recovery") };
            result = new RestoreEngine().Run(cancelTarget, copies, removals, new List<string>(), new List<string>(), fingerprints,
                recoveryConfig, new OperationContext(ui, cancellation.Token));
            Assert(result.Cancelled && File.ReadAllText(Path.Combine(cancelTarget, "Songs", "one.m8s")) == "KEEP ORIGINAL" &&
                File.ReadAllText(Path.Combine(cancelTarget, "extra.wav")) == "KEEP EXTRA", "cancelling recovery leaves every target file untouched");
            string recoveryFolder = Directory.GetDirectories(recoveryConfig.RecoveryFolder)[0];
            Assert(File.ReadAllText(Path.Combine(recoveryFolder, ".gearbackup", "recovery-status.txt")).Trim() == "incomplete",
                "cancelled recovery does not claim completion");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.SavingHistory) cancellation.Cancel(); };
            string cancelledHistory = Path.Combine(root, "cancel-history-save");
            result = new BackupEngine().Run(cancelSource, cancelledHistory, null, false, config, new OperationContext(ui, cancellation.Token));
            string interrupted = ListBackups(cancelledHistory)[0];
            Assert(result.Cancelled && File.Exists(Path.Combine(interrupted, "large.wav")) && BackupStatus(interrupted) == Status.Incomplete,
                "history cancellation retains completed browsable copies for resume");
            Assert(!Directory.Exists(SnapshotRoot(interrupted)), "history cancellation does not publish manifest");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            string untouched = Path.Combine(root, "pre-cancelled-history");
            result = new BackupEngine().Run(source, untouched, null, false, config, new OperationContext(new TestInteraction(), cancellation.Token));
            Assert(result.Cancelled && !Directory.Exists(untouched), "pre-cancelled operation makes no directories");
            Assert(new IntegrityEngine().Run(new List<string>(), new OperationContext(new TestInteraction(), cancellation.Token)).Cancelled,
                "pre-cancelled empty integrity request reports cancellation");
        }
        Assert(OperationContext.Current == context || OperationContext.Current.Interaction is ConsoleInteraction,
            "engine restores calling context after completion and cancellation");
    }
}
