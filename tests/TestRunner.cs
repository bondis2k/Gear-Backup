using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

class TestRunner
{
    static int Main()
    {
        try { M8Backup.RunTests(); return 0; }
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
    public static void RunTests()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".test-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Rooted(Path.Combine(root, "card"));
        string history = Path.Combine(root, "history");
        string title, summary;
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
        Throws(() => SafePath(root, "..\\escape.txt"), "traversal rejected");
        Throws(() => SafePath(root, "C:\\escape.txt"), "absolute manifest path rejected");

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
        Assert(BackupStatus(backup) == "complete", "full backup complete status");
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

        SetBackupStatus(backup, "incomplete");
        Put(Path.Combine(backup, "Songs", "one.m8s"), "PARTIAL");
        // Content comparison is explicitly opted in for resume repair tests.
        config.Thorough = true;
        Assert(Backup(source, history, backup, true, config, out title, out summary) == 0, "resume repairs partial file despite fast setting");
        Assert(File.ReadAllText(Path.Combine(backup, "Songs", "one.m8s")) == "BBBB", "resume restores source content");
        Assert(BackupStatus(backup) == "complete", "resume complete status");
        Assert(Directory.GetFiles(Path.Combine(backup, ".m8backup", "quarantine"), "*", SearchOption.AllDirectories).Length == 1, "partial copy quarantined");
        int checkedOk = 0, checkedDamaged = 0, checkedMissing = 0, uncheckedBackups = 0;
        CheckBackup(backup, ref checkedOk, ref checkedDamaged, ref checkedMissing, ref uncheckedBackups);
        Assert(checkedDamaged == 0 && checkedMissing == 0, "resume does not bless partial files as valid versions");
        // Simulate an interrupted incremental after the file was copied but before checksums were updated.
        SetBackupStatus(backup, "incomplete");
        Put(song, "CCCC"); File.SetLastWriteTimeUtc(song, oldTime);
        Put(Path.Combine(backup, "Songs", "one.m8s"), "CCCC");
        Assert(Backup(source, history, backup, true, config, out title, out summary) == 0, "resume refreshes stale checksum entries");
        Assert(LoadChecksums(backup)["Songs\\one.m8s"] == HashFile(song), "resumed checksum matches repaired file");

        string target = Rooted(Path.Combine(root, "restore-target"));
        Put(Path.Combine(target, "Songs", "one.m8s"), "CURRENT");
        Put(Path.Combine(target, "extra.txt"), "KEEP ME IN RECOVERY");
        var removals = new List<string> { "extra.txt" };
        config.Recovery = true; config.RecoveryFolder = Path.Combine(root, "custom-recovery");
        ExecuteRestore(target, first.Files, removals, first.Folders, new List<string>(), TargetHashes(target, first.Files, removals), config, out title, out summary);
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

        string noRecovery = Rooted(Path.Combine(root, "no-recovery-target"));
        Put(Path.Combine(noRecovery, "Songs", "one.m8s"), "NEWER");
        config.Recovery = false; config.RecoveryFolder = Path.Combine(root, "should-not-exist");
        var one = new List<RestoreEntry> { first.Files.Find(f => f.Relative == "Songs\\one.m8s") };
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

        // Exercise actual interactive restore selection with redirected input.
        string uiTarget = Rooted(Path.Combine(root, "ui-card"));
        Directory.CreateDirectory(uiTarget);
        Console.SetIn(new StringReader("1\n1\nb\n1\n"));
        Assert(Restore(uiTarget, new List<string> { backup }, config, out title, out summary) == 0, "bundle restore menu");
        Assert(File.Exists(Path.Combine(uiTarget, "Bundles", "demo", "demo.m8s")), "bundle song restored");
        Assert(File.Exists(Path.Combine(uiTarget, "Bundles", "demo", "kick.wav")), "bundle sample restored");
        Assert(!Directory.Exists(Path.Combine(uiTarget, "Songs")), "bundle restore limits scope");
        Put(Path.Combine(uiTarget, "extra.txt"), "leave untouched");
        PreviewOnly = true;
        Console.SetIn(new StringReader("1\n1\na\ne\n"));
        Assert(Restore(uiTarget, new List<string> { backup }, config, out title, out summary) == 0, "restore preview works");
        Assert(File.Exists(Path.Combine(uiTarget, "extra.txt")), "preview never deletes extras");
        Assert(!File.Exists(Path.Combine(uiTarget, "Songs", "one.m8s")), "preview never copies files");
        PreviewOnly = false;

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
        Assert(SongDependencies(projectEntry, first, target).Count == 2, "song selects sample from same snapshot");
        Throws(() => SongDependencies(projectEntry, new Snapshot(), target), "missing samples reject incomplete song restore");
        project[11] = 7; File.WriteAllBytes(projectPath, project);
        Throws(() => SamplePaths(projectPath), "unknown firmware refused");
        project[11] = 6;
        Array.Clear(project, 80446 + 87, 128);
        Encoding.ASCII.GetBytes("../outside.wav").CopyTo(project, 80446 + 87);
        File.WriteAllBytes(projectPath, project);
        Throws(() => SamplePaths(projectPath), "dependency traversal rejected");

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
        string namingHistory = Path.Combine(root, "same-day-fulls");
        Assert(Backup(source, namingHistory, null, false, config, out title, out summary) == 0, "first same-day full backup");
        string firstFull = ListBackups(namingHistory)[0];
        Assert(Backup(source, namingHistory, firstFull, false, config, out title, out summary) == 0, "second same-day full backup");
        var sameDay = ListBackups(namingHistory);
        Assert(sameDay.Count == 2 && sameDay[0] == firstFull && sameDay[1] != firstFull, "new full backup names sort chronologically without collision");

        // Test the exact-restore public planner and removal confirmation, rather than just its executor.
        string exactTarget = Rooted(Path.Combine(root, "exact-ui-target"));
        Put(Path.Combine(exactTarget, "extra.txt"), "remove this");
        Directory.CreateDirectory(Path.Combine(exactTarget, "extra-empty"));
        Console.SetIn(new StringReader("1\n1\na\ne\ny\n"));
        Assert(Restore(exactTarget, new List<string> { backup }, config, out title, out summary) == 0, "public exact restore workflow");
        Assert(!File.Exists(Path.Combine(exactTarget, "extra.txt")) && !Directory.Exists(Path.Combine(exactTarget, "extra-empty")), "public exact restore removes extra file and empty folder");
        // Latest snapshot only has five files; old deleted membership is not resurrected.
        Assert(!File.Exists(Path.Combine(exactTarget, "Songs", "removed.m8s")), "latest exact restore respects deletion membership");

        string blockedTarget = Rooted(Path.Combine(root, "blocked-target"));
        Put(Path.Combine(blockedTarget, "Songs"), "file blocks parent");
        Console.SetIn(new StringReader("1\n1\na\nm\n"));
        Throws(() => Restore(blockedTarget, new List<string> { backup }, config, out title, out summary), "restore parent conflict fails before target changes");
        Assert(!Directory.Exists(Path.Combine(blockedTarget, "Bundles")), "parent conflict preflight leaves target untouched");
        string growingTarget = Rooted(Path.Combine(root, "growing-files"));
        Put(Path.Combine(growingTarget, "a"), "AAAA"); Put(Path.Combine(growingTarget, "b"), "BBBB");
        var growing = new List<RestoreEntry> { new RestoreEntry { Relative = "a", Size = 12 }, new RestoreEntry { Relative = "b", Size = 16 },
            new RestoreEntry { Relative = "new", Size = 10 } };
        Assert(RestoreSpaceNeeded(growingTarget, growing) == 46, "space estimate includes accumulated growth and staging");

        // Keeping differing files disables exact removal, even if it was initially selected.
        Put(Path.Combine(exactTarget, "Songs", "one.m8s"), "MY NEW SONG");
        Put(Path.Combine(exactTarget, "keep-extra.txt"), "keep");
        Console.SetIn(new StringReader("1\n1\na\ne\nk\n"));
        Assert(Restore(exactTarget, new List<string> { backup }, config, out title, out summary) == 0, "keep-existing restore choice");
        Assert(File.ReadAllText(Path.Combine(exactTarget, "Songs", "one.m8s")) == "MY NEW SONG" && File.Exists(Path.Combine(exactTarget, "keep-extra.txt")), "keep mode preserves differing and extra files");

        string malicious = Path.Combine(root, "bad.manifest");
        Put(malicious, "M8SNAPSHOT\t1\nD\t" + Encode("..\\outside") + "\n");
        Throws(() => ReadSnapshot(backup, malicious), "malicious manifest rejected");
        int ok = 0, damaged = 0, missing = 0;
        CheckSnapshots(backup, ref ok, ref damaged, ref missing);
        Assert(damaged == 0 && missing == 0 && ok > 0, "history check succeeds");
        Put(one[0].StoredPath, "CORRUPTED OBJECT");
        Throws(() => ValidateEntry(one[0]), "damaged history refused before restore");
        CheckSnapshots(backup, ref ok, ref damaged, ref missing);
        Assert(damaged > 0, "history check detects damage");
        config.Thorough = true;
        // Corrupted object belongs to old snapshot; current-source objects can still be valid.
        // A selected current object corruption must fail snapshot creation and mark backup failed.
        RestoreEntry currentSong = LatestSnapshot(backup).Files.Find(f => f.Relative == "Songs\\one.m8s");
        Put(currentSong.StoredPath, "BAD");
        Assert(Backup(source, history, backup, true, config, out title, out summary) == 2, "history failure prevents successful backup");
        Assert(BackupStatus(backup) == "failed", "history failure sets failed status");
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
        config.Verify = false;
        Put(currentSong.StoredPath, "DDDD"); // Same size as the source; only its checksum differs.
        Assert(Backup(source, history, backup, true, config, out title, out summary) == 0, "Verify=no skips snapshot object checksum comparison");
        int optionalOk = 0, optionalDamaged = 0, optionalMissing = 0;
        CheckSnapshots(backup, ref optionalOk, ref optionalDamaged, ref optionalMissing);
        Assert(optionalDamaged > 0, "explicit integrity check still detects corruption with defaults off");
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
        string blockA = Path.Combine(root, "blocks-a.bin"), blockB = Path.Combine(root, "blocks-b.bin");
        byte[] blockBytes = new byte[131077];
        File.WriteAllBytes(blockA, blockBytes); File.WriteAllBytes(blockB, blockBytes);
        Assert(FilesEqual(blockA, blockB), "byte comparison spans multiple buffers");
        blockBytes[131076] = 1; File.WriteAllBytes(blockB, blockBytes);
        Assert(!FilesEqual(blockA, blockB), "byte comparison finds difference in final partial buffer");
        TestEngineOperations(root);
        using (LockHistory(history)) Throws(() => LockHistory(history), "concurrent history writer rejected");
        Console.WriteLine("PASS: " + assertions + " assertions. Fixtures: " + root);
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
        public string ReadLine() { throw new Exception("Engine unexpectedly requested console-style input"); }
        public char ChooseKey(string question, string allowed) { return allowed[0]; }
        public int ChooseNumber(string question, int max) { throw new Exception("Engine unexpectedly requested backup selection"); }
        public bool ReviewPlan(OperationPlan plan) { Plans++; return !plan.PreviewOnly; }
        public void ReportProgress(OperationProgress progress)
        {
            Progress.Add(progress);
            if (OnProgress != null) OnProgress(progress);
        }
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
            if (!delayed && p.Stage == "Saving history") { delayed = true; Thread.Sleep(350); }
        };
        var context = new OperationContext(ui, CancellationToken.None);
        OperationResult result = new BackupEngine().Run(source, history, null, false, config, context);
        Assert(result.ExitCode == 0 && !result.Cancelled && ui.Plans == 1, "backup engine uses injected plan review without console input");
        Assert(ui.Progress.Exists(p => p.Stage == "Scanning") && ui.Progress.Exists(p => p.Stage == "Copying") &&
            ui.Progress.Exists(p => p.Stage == "Saving history"), "structured backup progress covers scan, copy, and history");
        Assert(!ui.Progress.Exists(p => p.Stage == "Hashing"), "default full backup avoids standalone preflight hashing reads");
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
        Assert(ui.Progress.FindAll(p => p.Stage == "Hashing" && p.Path == sharedObject && p.Bytes == p.TotalBytes).Count == 1,
            "shared snapshot object verified only once per run");
        Assert(ui.Progress.FindAll(p => p.Stage == "Hashing" && p.Path == Path.Combine(source, "Songs", "one.m8s") && p.Bytes == p.TotalBytes).Count == 1,
            "thorough planning reuses source hash instead of rehashing for space estimate");
        Assert(result.ExitCode == 0 && ui.Plans == 0, "unchanged incremental needs no injected confirmation");

        string target = Rooted(Path.Combine(root, "engine-target"));
        Directory.CreateDirectory(target);
        ui = new TestInteraction();
        result = new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, 'a', null, false,
            config, new OperationContext(ui, CancellationToken.None));
        Assert(result.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "HEADLESS",
            "restore planning and execution run through injected frontend");
        Assert(ui.Plans == 1 && ui.Progress.Exists(p => p.Stage == "Planning restore"), "restore engine reports planning and reviews plan");

        string cancelSource = Rooted(Path.Combine(root, "cancel-source"));
        FileInfo large = new FileInfo(Path.Combine(cancelSource, "large.wav"));
        Directory.CreateDirectory(cancelSource);
        File.WriteAllBytes(large.FullName, new byte[2 * 1024 * 1024]);
        string cancelHistory = Path.Combine(root, "cancel-history");
        config = new Config { BackupFolder = root, Preview = false };
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == "Copying") cancellation.Cancel(); };
            result = new BackupEngine().Run(cancelSource, cancelHistory, null, false, config, new OperationContext(ui, cancellation.Token));
            string interrupted = ListBackups(cancelHistory)[0];
            Assert(result.Cancelled && result.ExitCode == 0 && BackupStatus(interrupted) == "incomplete", "cancelled backup remains resumable");
            Assert(!File.Exists(Path.Combine(interrupted, "large.wav")), "cancelling during copy removes partial backup file");
        }

        // Cancelling a changed incremental copy restores its previous browsable version.
        Put(Path.Combine(source, "Songs", "one.m8s"), "CHANGED CONTENT");
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == "Copying") cancellation.Cancel(); };
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
            ui.OnProgress = p => { if (p.Stage == "Copying") cancellation.Cancel(); };
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
            ui.OnProgress = p => { if (p.Stage == "Hashing") cancellation.Cancel(); };
            result = new IntegrityEngine().Run(new List<string> { backup }, new OperationContext(ui, cancellation.Token));
            Assert(result.Cancelled && result.Title == "M8 check cancelled", "integrity cancellation is not reported as corruption or success");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            ui = new TestInteraction();
            ui.OnProgress = p => { if (p.Stage == "Copying") cancellation.Cancel(); };
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
            ui.OnProgress = p => { if (p.Stage == "Saving history") cancellation.Cancel(); };
            string cancelledHistory = Path.Combine(root, "cancel-history-save");
            result = new BackupEngine().Run(cancelSource, cancelledHistory, null, false, config, new OperationContext(ui, cancellation.Token));
            string interrupted = ListBackups(cancelledHistory)[0];
            Assert(result.Cancelled && File.Exists(Path.Combine(interrupted, "large.wav")) && BackupStatus(interrupted) == "incomplete",
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
    static void Finish(string pnp, string title, string summary, string mode) { }
}
