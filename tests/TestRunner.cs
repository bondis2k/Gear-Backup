using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

class TestRunner
{
    static int Main()
    {
        try { return M8Backup.RunTests(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

partial class M8Backup
{
    static int assertions;
    static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("ASSERTION FAILED: " + message);
    }
    // Fixtures are kept after a failure (Summarize skips this) or when M8_KEEP_TEST_FIXTURES=1.
    static void RemoveFixtures(string root)
    {
        if (Environment.GetEnvironmentVariable("M8_KEEP_TEST_FIXTURES") == "1")
        {
            Console.WriteLine("Fixtures kept: " + root);
            return;
        }
        try { Directory.Delete(root, true); }
        catch (Exception ex) { Console.WriteLine("Could not remove fixtures " + root + ": " + ex.Message); }
    }
    static void Throws(Action action, string message)
    {
        bool threw = false;
        try { action(); } catch (IOException) { threw = true; } catch (ArgumentException) { threw = true; }
        Assert(threw, message);
    }
    static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
    static Snapshot LatestSnapshot(string backup)
    {
        string[] manifests = Directory.GetFiles(SnapshotRoot(backup), "*.manifest");
        Array.Sort(manifests, StringComparer.Ordinal);
        return ReadSnapshot(backup, manifests[manifests.Length - 1]);
    }
    static Dictionary<string, string> TargetHashes(string target, List<RestoreEntry> copies, List<string> removals)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (RestoreEntry item in copies)
        {
            string path = SafePath(target, item.Relative);
            result[item.Relative] = Fingerprint(path, true);
        }
        foreach (string path in removals) result[path] = Fingerprint(SafePath(target, path), true);
        return result;
    }
    // ---- Test runner: named, independent tests; a failure is reported and the run continues ----

    static readonly List<string> results = new List<string>();
    static int failedTests;

    // Runs one test. A failure is recorded and the next test still runs. The global test state a test
    // may change (preview mode, console input) is reset afterwards so it can't leak into the next one.
    static void RunTest(string name, Action test)
    {
        int before = assertions;
        TextReader input = Console.In;
        try
        {
            test();
            results.Add(string.Format("  ok    {0} ({1} checks)", name, assertions - before));
        }
        catch (Exception ex)
        {
            failedTests++;
            string reason = ex.Message.StartsWith("ASSERTION FAILED") ? ex.Message : ex.GetType().Name + ": " + ex.Message;
            // The first stack line outside the Assert/Throws helpers is the check that failed.
            string where = "";
            foreach (string frame in (ex.StackTrace ?? "").Split('\n'))
                if (!frame.Contains(".Assert(") && !frame.Contains(".Throws(")) { where = frame.Trim(); break; }
            results.Add(string.Format("  FAIL  {0}\n          {1}\n          {2}", name, reason, where));
        }
        finally
        {
            PreviewOnly = false;
            Console.SetIn(input);
        }
    }

    // Prints every test's result and returns the exit code. Fixtures are removed only when everything passed.
    static int Summarize(string root)
    {
        Console.WriteLine();
        Console.WriteLine("Test results:");
        foreach (string line in results) Console.WriteLine(line);
        Console.WriteLine();
        if (failedTests == 0)
        {
            Console.WriteLine("PASS: " + results.Count + " tests, " + assertions + " assertions.");
            RemoveFixtures(root);
            return 0;
        }
        Console.WriteLine("FAIL: " + failedTests + " of " + results.Count + " tests failed. Fixtures kept: " + root);
        return 1;
    }

    public static int RunTests()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".test-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunTest("Settings and profiles", () => TestSettingsAndProfiles(Folder(root, "settings")));
        RunTest("Full and incremental backup", () => TestFullAndIncremental(Folder(root, "backup")));
        RunTest("Resume repairs partial copies", () => TestResume(Folder(root, "resume")));
        RunTest("Point-in-time restore with recovery", () => TestHistoricalRestore(Folder(root, "historical")));
        RunTest("Restore safety: no recovery, locked files, changed targets", () => TestRestoreSafety(Folder(root, "restore-safety")));
        RunTest("Interactive bundle restore and restore preview", () => TestRestoreMenus(Folder(root, "restore-menus")));
        RunTest("Song dependencies", () => TestSongDependencies(Folder(root, "dependencies")));
        RunTest("Command-line entry flow", () => TestCommandLine(Folder(root, "cli")));
        RunTest("Exact and keep restore workflows", () => TestExactAndKeep(Folder(root, "exact")));
        RunTest("History integrity and damage", () => TestHistoryIntegrity(Folder(root, "integrity")));
        RunTest("Verify=no behaviour", () => TestVerifyOff(Folder(root, "verify-off")));
        RunTest("Undated files", () => TestUndatedFiles(Folder(root, "undated")));
        RunTest("Byte comparison, drive space and settings copy", () => TestSmallHelpers(Folder(root, "small")));
        RunTest("History lock", () => TestHistoryLock(Folder(root, "lock")));
        RunTest("Engine operations", () => TestEngineOperations(Folder(root, "engine")));
        RunTest("Optimizations", () => TestOptimizations(Folder(root, "optimizations")));
        RunTest("Copy pipeline", () => TestCopyPipeline(Folder(root, "pipeline")));
        RunTest("Zip", () => TestZip(Folder(root, "zip")));
        TestHelpers(root);
        return Summarize(root);
    }

    static string Folder(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- Shared fixture ----

    // A test card backed up into its own history. With `edited`, a second session follows: one.m8s is
    // changed (same size and time, so only content comparison sees it), removed.m8s deleted, and an
    // incremental backup made.
    sealed class Fixture
    {
        public string Root, Source, History, Backup;
        public Snapshot First, Latest;
        public Config Config;
        public string Song { get { return Path.Combine(Source, "Songs", "one.m8s"); } }
    }

    static Fixture BackedUpCard(string root, bool edited)
    {
        var f = new Fixture { Root = root, Source = Rooted(Path.Combine(root, "card")), History = Path.Combine(root, "history") };
        f.Config = new Config { BackupFolder = root, Preview = false, Thorough = true, Verify = true };
        Put(Path.Combine(f.Source, "Songs", "one.m8s"), "AAAA");
        Put(Path.Combine(f.Source, "Songs", "removed.m8s"), "REMOVED");
        Put(Path.Combine(f.Source, "Songs", "dated_2026-01-01_120000.m8s"), "REAL DATED SONG");
        Put(Path.Combine(f.Source, "Samples", "kick.wav"), "WAVE");
        Put(Path.Combine(f.Source, "Bundles", "demo", "demo.m8s"), "DEMO");
        Put(Path.Combine(f.Source, "Bundles", "demo", "kick.wav"), "WAVE");
        Put(Path.Combine(f.Source, "Thumbs.db"), "ignored");
        Directory.CreateDirectory(Path.Combine(f.Source, "Empty"));
        string title, summary;
        if (Backup(f.Source, f.History, null, false, f.Config, out title, out summary) != 0) throw new Exception("fixture full backup failed");
        f.Backup = ListBackups(f.History)[0];
        f.First = f.Latest = LatestSnapshot(f.Backup);
        if (edited)
        {
            DateTime time = File.GetLastWriteTimeUtc(f.Song);
            Put(f.Song, "BBBB"); File.SetLastWriteTimeUtc(f.Song, time);
            File.Delete(Path.Combine(f.Source, "Songs", "removed.m8s"));
            if (Backup(f.Source, f.History, f.Backup, true, f.Config, out title, out summary) != 0) throw new Exception("fixture incremental backup failed");
            f.Latest = LatestSnapshot(f.Backup);
        }
        return f;
    }

    // ---- Tests ----

    static void TestSettingsAndProfiles(string root)
    {
        string source = Rooted(Path.Combine(root, "card"));
        var config = new Config { BackupFolder = root, Preview = false, Thorough = true, Verify = true };
        Assert(!new Config().Verify && !new Config().Thorough, "SHA-256 comparisons default off");
        Assert(!config.Recovery, "recovery defaults to no");
        Assert(RecoveryRoot(config) == Path.Combine(root, "M8 Recovery"), "empty recovery location defaults under backup folder");
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
        int objects = Directory.GetFiles(Path.Combine(backup, ".m8backup", "objects")).Length;
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
        Assert(Directory.GetFiles(Path.Combine(f.Backup, ".m8backup", "quarantine"), "*", SearchOption.AllDirectories).Length == 1, "partial copy quarantined");
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

    static void TestHistoricalRestore(string root)
    {
        Fixture f = BackedUpCard(root, true);
        string title, summary;
        Config config = f.Config;
        string target = Rooted(Path.Combine(root, "restore-target"));
        Put(Path.Combine(target, "Songs", "one.m8s"), "CURRENT");
        Put(Path.Combine(target, "extra.txt"), "KEEP ME IN RECOVERY");
        var removals = new List<string> { "extra.txt" };
        config.Recovery = true; config.RecoveryFolder = Path.Combine(root, "custom-recovery");
        ExecuteRestore(target, f.First.Files, removals, f.First.Folders, new List<string>(), TargetHashes(target, f.First.Files, removals), config, out title, out summary);
        Assert(title == "M8 restore complete", "historical restore complete");
        Assert(File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "AAAA", "point in time restores old song");
        Assert(File.Exists(Path.Combine(target, "Songs", "removed.m8s")), "point in time restores subsequently deleted song");
        Assert(File.Exists(Path.Combine(target, "Songs", "dated_2026-01-01_120000.m8s")), "historical restore keeps real dated songs");
        Assert(!File.Exists(Path.Combine(target, "extra.txt")), "exact removes extras");
        Assert(Directory.Exists(Path.Combine(target, "Empty")), "restore preserves empty folders");
        string recovery = Directory.GetDirectories(config.RecoveryFolder)[0];
        Assert(File.ReadAllText(Path.Combine(recovery, "Songs", "one.m8s")) == "CURRENT", "recovery saves overwritten song");
        Assert(File.ReadAllText(Path.Combine(recovery, "extra.txt")) == "KEEP ME IN RECOVERY", "recovery saves exact-removal file");
        Assert(File.ReadAllText(Path.Combine(recovery, ".m8backup", "recovery-status.txt")).StartsWith("complete"), "recovery status complete");
    }

    static void TestRestoreSafety(string root)
    {
        Fixture f = BackedUpCard(root, false);
        string title, summary;
        Config config = f.Config;
        string noRecovery = Rooted(Path.Combine(root, "no-recovery-target"));
        Put(Path.Combine(noRecovery, "Songs", "one.m8s"), "NEWER");
        config.Recovery = false; config.RecoveryFolder = Path.Combine(root, "should-not-exist");
        var one = new List<RestoreEntry> { f.First.Files.Find(e => e.Relative == "Songs\\one.m8s") };
        ExecuteRestore(noRecovery, one, new List<string>(), new List<string>(), new List<string>(), TargetHashes(noRecovery, one, new List<string>()), config, out title, out summary);
        Assert(!Directory.Exists(config.RecoveryFolder), "Recovery=no creates no recovery folder");
        Assert(File.ReadAllText(Path.Combine(noRecovery, "Songs", "one.m8s")) == "AAAA", "restore works with recovery disabled");
        Assert(Directory.GetFiles(noRecovery, ".m8rollback-*", SearchOption.AllDirectories).Length == 0, "successful replacements clean transient rollback files");
        string lockedTarget = Path.Combine(root, "locked.txt"), stagedFile = Path.Combine(root, "staged.txt");
        Put(lockedTarget, "ORIGINAL"); Put(stagedFile, "REPLACEMENT");
        using (var locked = new FileStream(lockedTarget, FileMode.Open, FileAccess.Read, FileShare.None))
            Throws(() => ReplaceSafely(stagedFile, lockedTarget), "locked replacement fails safely");
        Assert(File.ReadAllText(lockedTarget) == "ORIGINAL" && File.ReadAllText(stagedFile) == "REPLACEMENT", "replacement failure retains both contents");

        Put(Path.Combine(noRecovery, "Songs", "one.m8s"), "PROTECT");
        config.Recovery = true; config.RecoveryFolder = Path.Combine(root, "blocked-recovery");
        Put(config.RecoveryFolder, "not a directory");
        Throws(() => ExecuteRestore(noRecovery, one, new List<string>(), new List<string>(), new List<string>(),
            TargetHashes(noRecovery, one, new List<string>()), config, out title, out summary), "recovery failure aborts restore");
        Assert(File.ReadAllText(Path.Combine(noRecovery, "Songs", "one.m8s")) == "PROTECT", "recovery failure leaves target untouched");
        config.RecoveryFolder = noRecovery;
        Throws(() => SaveRecovery(noRecovery, one, new List<string>(), TargetHashes(noRecovery, one, new List<string>()), config), "recovery destination cannot be target");
        config.Recovery = false;
        var beforeChange = TargetHashes(noRecovery, one, new List<string>());
        Put(Path.Combine(noRecovery, "Songs", "one.m8s"), "CHANGED DURING PREVIEW");
        ExecuteRestore(noRecovery, one, new List<string>(), new List<string>(), new List<string>(), beforeChange, config, out title, out summary);
        Assert(title != "M8 restore complete", "target change rejected");
        Assert(File.ReadAllText(Path.Combine(noRecovery, "Songs", "one.m8s")) == "CHANGED DURING PREVIEW", "changed target preserved");

        string growingTarget = Rooted(Path.Combine(root, "growing-files"));
        Put(Path.Combine(growingTarget, "a"), "AAAA"); Put(Path.Combine(growingTarget, "b"), "BBBB");
        var growing = new List<RestoreEntry> { new RestoreEntry { Relative = "a", Size = 12 }, new RestoreEntry { Relative = "b", Size = 16 },
            new RestoreEntry { Relative = "new", Size = 10 } };
        Assert(RestoreSpaceNeeded(growingTarget, growing) == 46, "space estimate includes accumulated growth and staging");
    }

    static void TestRestoreMenus(string root)
    {
        Fixture f = BackedUpCard(root, true);
        string title, summary;
        // Exercise actual interactive restore selection with redirected input.
        string uiTarget = Rooted(Path.Combine(root, "ui-card"));
        Directory.CreateDirectory(uiTarget);
        Console.SetIn(new StringReader("1\n1\nb\n1\n"));
        Assert(Restore(uiTarget, new List<string> { f.Backup }, f.Config, out title, out summary) == 0, "bundle restore menu");
        Assert(File.Exists(Path.Combine(uiTarget, "Bundles", "demo", "demo.m8s")), "bundle song restored");
        Assert(File.Exists(Path.Combine(uiTarget, "Bundles", "demo", "kick.wav")), "bundle sample restored");
        Assert(!Directory.Exists(Path.Combine(uiTarget, "Songs")), "bundle restore limits scope");
        Put(Path.Combine(uiTarget, "extra.txt"), "leave untouched");
        PreviewOnly = true;
        Console.SetIn(new StringReader("1\n1\na\ne\n"));
        Assert(Restore(uiTarget, new List<string> { f.Backup }, f.Config, out title, out summary) == 0, "restore preview works");
        Assert(File.Exists(Path.Combine(uiTarget, "extra.txt")), "preview never deletes extras");
        Assert(!File.Exists(Path.Combine(uiTarget, "Songs", "one.m8s")), "preview never copies files");
    }

    static void TestSongDependencies(string root)
    {
        Fixture f = BackedUpCard(root, false);
        string target = Rooted(Path.Combine(root, "target"));
        // Synthetic, bounded firmware-6 project verifies parser offsets without real device files.
        byte[] project = new byte[80446 + 215 * 128];
        Encoding.ASCII.GetBytes("M8VERSION").CopyTo(project, 0); project[11] = 6;
        for (int i = 0; i < 128; i++) project[80446 + i * 215] = 255;
        project[80446] = 2;
        Encoding.ASCII.GetBytes("/Samples/kick.wav").CopyTo(project, 80446 + 87);
        string projectPath = Path.Combine(root, "project.m8s");
        File.WriteAllBytes(projectPath, project);
        Assert(SamplePaths(projectPath)[0] == "Samples\\kick.wav", "sample dependency path parsed");
        var projectEntry = new RestoreEntry { Relative = "Songs\\project.m8s", StoredPath = projectPath, Hash = HashFile(projectPath), Size = project.Length };
        Assert(SongDependencies(projectEntry, f.First, target).Count == 2, "song selects sample from same snapshot");
        Throws(() => SongDependencies(projectEntry, new Snapshot(), target), "missing samples reject incomplete song restore");
        project[11] = 7; File.WriteAllBytes(projectPath, project);
        Throws(() => SamplePaths(projectPath), "unknown firmware refused");
        project[11] = 6;
        Array.Clear(project, 80446 + 87, 128);
        Encoding.ASCII.GetBytes("../outside.wav").CopyTo(project, 80446 + 87);
        File.WriteAllBytes(projectPath, project);
        Throws(() => SamplePaths(projectPath), "dependency traversal rejected");
    }

    static void TestCommandLine(string root)
    {
        string source = Rooted(Path.Combine(root, "card"));
        Put(Path.Combine(source, "Songs", "one.m8s"), "AAAA");
        Put(Path.Combine(source, "Samples", "kick.wav"), "WAVE");
        // Run the real argument/source/profile entry flow against a private INI beside the test executable.
        string testIni = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "M8Backup.ini");
        Put(testIni, "BackupFolder=" + root + "\nMode=full\nPreview=no\nRecovery=no\nThorough=yes\n");
        Assert(Run(false, false, new[] { "--source", source, "--profile", "card-A" }) == 0, "CLI manual source full backup");
        Assert(ListBackups(Path.Combine(root, "M8 Backups", "profiles", "card-A")).Count == 1, "CLI named profile uses separate history");
        Assert(Run(false, false, new[] { "--source", source, "--profile", "card-B" }) == 0, "second CLI profile backup");
        Assert(ListBackups(Path.Combine(root, "M8 Backups", "profiles", "card-B")).Count == 1, "second profile separated");
        string iniBeforePreview = File.ReadAllText(testIni);
        PreviewOnly = true;
        Assert(Run(false, false, new[] { "--source", source, "--profile", "preview-card", "--preview" }) == 0, "CLI preview entry flow");
        Assert(!Directory.Exists(Path.Combine(root, "M8 Backups", "profiles", "preview-card")), "CLI preview creates no history or lock");
        Assert(File.ReadAllText(testIni) == iniBeforePreview, "preview does not update INI");
        PreviewOnly = false;
        Console.SetIn(new StringReader("1\n"));
        Assert(Run(false, true, new[] { "--check", "--profile", "card-A" }) == 0, "CLI checks profile without source/device");
        Throws(() => Run(false, false, new[] { "--source" }), "CLI missing argument rejected");
        Throws(() => Run(false, false, new[] { "--invalid" }), "CLI unknown argument rejected");
        Throws(() => Run(false, false, new[] { "--source", root }), "overlapping source and backup rejected");
    }

    static void TestExactAndKeep(string root)
    {
        Fixture f = BackedUpCard(root, true);
        string title, summary;
        // Test the exact-restore public planner and removal confirmation, rather than just its executor.
        string exactTarget = Rooted(Path.Combine(root, "exact-ui-target"));
        Put(Path.Combine(exactTarget, "extra.txt"), "remove this");
        Directory.CreateDirectory(Path.Combine(exactTarget, "extra-empty"));
        Console.SetIn(new StringReader("1\n1\na\ne\ny\n"));
        Assert(Restore(exactTarget, new List<string> { f.Backup }, f.Config, out title, out summary) == 0, "public exact restore workflow");
        Assert(!File.Exists(Path.Combine(exactTarget, "extra.txt")) && !Directory.Exists(Path.Combine(exactTarget, "extra-empty")), "public exact restore removes extra file and empty folder");
        // Latest snapshot only has five files; old deleted membership is not resurrected.
        Assert(!File.Exists(Path.Combine(exactTarget, "Songs", "removed.m8s")), "latest exact restore respects deletion membership");

        string blockedTarget = Rooted(Path.Combine(root, "blocked-target"));
        Put(Path.Combine(blockedTarget, "Songs"), "file blocks parent");
        Console.SetIn(new StringReader("1\n1\na\nm\n"));
        Throws(() => Restore(blockedTarget, new List<string> { f.Backup }, f.Config, out title, out summary), "restore parent conflict fails before target changes");
        Assert(!Directory.Exists(Path.Combine(blockedTarget, "Bundles")), "parent conflict preflight leaves target untouched");

        // Keeping differing files disables exact removal, even if it was initially selected.
        Put(Path.Combine(exactTarget, "Songs", "one.m8s"), "MY NEW SONG");
        Put(Path.Combine(exactTarget, "keep-extra.txt"), "keep");
        Console.SetIn(new StringReader("1\n1\na\ne\nk\n"));
        Assert(Restore(exactTarget, new List<string> { f.Backup }, f.Config, out title, out summary) == 0, "keep-existing restore choice");
        Assert(File.ReadAllText(Path.Combine(exactTarget, "Songs", "one.m8s")) == "MY NEW SONG" && File.Exists(Path.Combine(exactTarget, "keep-extra.txt")), "keep mode preserves differing and extra files");
    }

    static void TestHistoryIntegrity(string root)
    {
        Fixture f = BackedUpCard(root, true);
        string title, summary;
        var one = new List<RestoreEntry> { f.First.Files.Find(e => e.Relative == "Songs\\one.m8s") };
        string malicious = Path.Combine(root, "bad.manifest");
        Put(malicious, "M8SNAPSHOT\t1\nD\t" + Encode("..\\outside") + "\n");
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
        Assert(title == "M8 restore complete" && File.ReadAllText(Path.Combine(optionalTarget, "optional.bin")) == "BBBB", "Verify=no permits restore without stored-checksum comparison");
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

    sealed class TestInteraction : IOperationInteraction
    {
        public readonly StringBuilder Output = new StringBuilder();
        public readonly List<OperationProgress> Progress = new List<OperationProgress>();
        public Action<OperationProgress> OnProgress;
        public int Plans;
        public void Write(string text) { Output.Append(text); }
        public void WriteLine(string text) { Output.AppendLine(text); }
        public void WriteColored(string text, ConsoleColor color) { WriteLine(text); }
        public string AskText(string question) { throw new Exception("Engine unexpectedly asked for text: " + question); }
        // Scripted answers for questions, in order; once used up, the first allowed key is chosen.
        public readonly Queue<char> Keys = new Queue<char>();
        public readonly List<string> Questions = new List<string>();
        public readonly List<IList<Option>> Options = new List<IList<Option>>();
        public char Choose(string question, IList<Option> options)
        {
            Questions.Add(question);
            Options.Add(options);
            return Keys.Count > 0 ? Keys.Dequeue() : options[0].Key;
        }
        // Scripted answers for picks from a list (0 = none); without it, a pick is a test failure.
        public Func<string, IList<string>, int> OnChooseItem;
        public int ChooseItem(string question, IList<string> items, string noneLabel)
        {
            if (OnChooseItem != null) return OnChooseItem(question, items);
            throw new Exception("Engine unexpectedly asked to pick from: " + question);
        }
        public bool ReviewPlan(OperationPlan plan) { Plans++; return !plan.PreviewOnly; }
        public void ReportProgress(OperationProgress progress)
        {
            Progress.Add(progress);
            if (OnProgress != null) OnProgress(progress);
        }
    }

    // Returns `firstBytes` bytes, then fails like a card being unplugged mid-copy.
    sealed class FailingStream : Stream
    {
        int remaining;
        public FailingStream(int firstBytes) { remaining = firstBytes; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0) throw new IOException("card unplugged");
            int n = Math.Min(count, remaining);
            remaining -= n;
            return n;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
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
            Assert(!new List<string>(names).Exists(n => n.StartsWith(".m8backup")), "zip leaves out the history store");
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
        Put(Path.Combine(backup, ".m8backup", "objects", "not-a-user-file"), "HIDDEN");
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
            Assert(Directory.GetFiles(cancelTarget, ".m8restore-*.tmp", SearchOption.AllDirectories).Length == 0,
                "cancelled restore cleans staged file");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == Stage.Hashing) cancellation.Cancel(); };
            result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(ui, cancellation.Token));
            Assert(result.Cancelled && result.Title == "M8 check cancelled", "integrity cancellation is not reported as corruption or success");
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
            Assert(File.ReadAllText(Path.Combine(recoveryFolder, ".m8backup", "recovery-status.txt")).Trim() == "incomplete",
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
    // Tests exclude Windows toast code to avoid registry writes/desktop notifications.
    static bool? ShowToast(string title, string summary, bool ask) { return false; }
    static void Finish(string pnp, string title, string summary, EjectMode mode) { }
}
