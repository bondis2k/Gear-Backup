using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
            result[item.Relative] = File.Exists(path) ? HashFile(path) : null;
        }
        foreach (string path in removals) result[path] = HashFile(SafePath(target, path));
        return result;
    }
    public static void RunTests()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".test-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Rooted(Path.Combine(root, "card"));
        string history = Path.Combine(root, "history");
        string title, summary;
        var config = new Config { BackupFolder = root, Preview = false, Thorough = true };
        Assert(!config.Recovery, "recovery defaults to no");
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
        config.Thorough = false;
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
        using (LockHistory(history)) Throws(() => LockHistory(history), "concurrent history writer rejected");
        Console.WriteLine("PASS: " + assertions + " assertions. Fixtures: " + root);
    }
    // Tests exclude Windows toast code to avoid registry writes/desktop notifications.
    static bool? ShowToast(string title, string summary, bool ask) { return false; }
    static void Finish(string pnp, string title, string summary, string mode) { }
}
