// Restores: snapshots, safety checks, menus, song dependencies, exact restores and keeping
// differing files.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using static FileCopy;
using static PathSafety;

partial class GearBackup
{
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
        Assert(title == "Gear Backup restore complete", "historical restore complete");
        Assert(File.ReadAllText(Path.Combine(target, "Songs", "one.m8s")) == "AAAA", "point in time restores old song");
        Assert(File.Exists(Path.Combine(target, "Songs", "removed.m8s")), "point in time restores subsequently deleted song");
        Assert(File.Exists(Path.Combine(target, "Songs", "dated_2026-01-01_120000.m8s")), "historical restore keeps real dated songs");
        Assert(!File.Exists(Path.Combine(target, "extra.txt")), "exact removes extras");
        Assert(Directory.Exists(Path.Combine(target, "Empty")), "restore preserves empty folders");
        string recovery = Directory.GetDirectories(config.RecoveryFolder)[0];
        Assert(File.ReadAllText(Path.Combine(recovery, "Songs", "one.m8s")) == "CURRENT", "recovery saves overwritten song");
        Assert(File.ReadAllText(Path.Combine(recovery, "extra.txt")) == "KEEP ME IN RECOVERY", "recovery saves exact-removal file");
        Assert(File.ReadAllText(Path.Combine(recovery, ".gearbackup", "recovery-status.txt")).StartsWith("complete"), "recovery status complete");
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
        Assert(Directory.GetFiles(noRecovery, ".gearrollback-*", SearchOption.AllDirectories).Length == 0, "successful replacements clean transient rollback files");
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
        Assert(title != "Gear Backup restore complete", "target change rejected");
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
}
