using System;
using System.IO;
using System.Threading;
using static Backups;
using static BackupZip;
using static FileCopy;
using static PathSafety;

partial class Tests
{
    [Test("Security: restore folder changed after preview", Folder = "helpers")]
    static void TestRestoreFolderSubstitution(string dir)
    {
        string root = Folder(dir, "restore-substitution");
        string target = Folder(root, "target"), child = Folder(target, "Songs"), outside = Folder(root, "outside");
        string stored = Path.Combine(root, "stored.txt");
        Put(stored, "RESTORED");
        var snapshot = new Snapshot { Name = "probe" };
        snapshot.Files.Add(new RestoreEntry { Relative = @"Songs\new.txt", StoredPath = stored, Size = 8,
            ModifiedTicks = DateTime.UtcNow.Ticks, CreatedTicks = DateTime.UtcNow.Ticks });
        var ui = new TestInteraction();
        ui.OnReview = plan => {
            Directory.Move(child, Path.Combine(target, "original-Songs"));
            CreateTestJunction(child, outside);
        };
        try
        {
            Throws(() => new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, RestoreMode.All, null, false,
                new Config { SingleContentStore = false, Preview = true }, new OperationContext(ui, CancellationToken.None)),
                "restore rejects a child replaced with a junction during review");
            Assert(ui.Plans == 1 && Directory.GetFileSystemEntries(outside).Length == 0,
                "approved preview never writes through the substituted folder");
        }
        finally { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child); }

        // Root checks must also be fresh when reusing the same operation context.
        string checkedRoot = Folder(root, "checked-root");
        WithUi(() => {
            SafePath(checkedRoot, "new.txt");
            Directory.Move(checkedRoot, Path.Combine(root, "original-root"));
            CreateTestJunction(checkedRoot, outside);
            try { Throws(() => SafePath(checkedRoot, "new.txt"), "a previously checked root cannot become a junction"); }
            finally { Directory.Delete(checkedRoot); }
        });
    }

    [Test("Security: statistics hard links", Folder = "helpers")]
    static void TestStatisticsHardLink(string dir)
    {
        string root = Folder(dir, "statistics-hard-link"), source = Folder(root, "source");
        string history = Path.Combine(root, "history"), victim = Path.Combine(root, "victim.txt");
        Put(Path.Combine(source, "song.txt"), "SONG"); Put(victim, "UNRELATED");
        bool planted = false;
        var ui = new TestInteraction();
        ui.OnProgress = p => {
            if (planted || p.Stage != Stage.Copying) return;
            Assert(TryHardLink(victim, Path.Combine(ListBackups(history)[0], "backup-statistics.txt")),
                "statistics fixture hard link created");
            planted = true;
        };
        OperationResult result;
        using (new OperationContext(ui, CancellationToken.None).Activate())
            result = RunBackup(source, history, null, false, new Config { SingleContentStore = false, Preview = false, MaxNameLength = 0 });
        string stats = Path.Combine(ListBackups(history)[0], "backup-statistics.txt");
        Assert(planted && result.ExitCode == 0, "backup completes with a planted statistics hard link");
        Assert(File.ReadAllText(victim) == "UNRELATED", "statistics replacement preserves the other hard-link name");
        Assert(File.ReadAllText(stats).Contains("statistics"), "backup receives its own statistics contents");
    }

    [Test("Security: ZIP metadata symbolic links", Folder = "helpers")]
    static void TestZipMetadataLinks(string dir)
    {
        string root = Folder(dir, "zip-metadata-links"), backup = Folder(root, "backup");
        string secret = Path.Combine(root, "secret.txt"); Put(secret, "AUDIT-SECRET");
        foreach (string name in new[] { "backup-statistics-extra.txt", "checksums.sha256" })
        {
            string link = Path.Combine(backup, name);
            CreateTestFileSymlink(link, secret);
            try
            {
                Throws(() => WithUi(() => ZipBackup(backup)), "ZIP rejects linked metadata: " + name);
                Assert(Directory.GetFiles(root, "*.zip*").Length == 0, "linked metadata leaves no archive or staging file");
            }
            finally { File.Delete(link); }
        }

        // A metadata entry can change after the initial enumeration, too.
        string first = Path.Combine(backup, "backup-statistics-a.txt"), second = Path.Combine(backup, "backup-statistics-b.txt");
        Put(first, "FIRST"); Put(second, "SECOND");
        string substituted = null;
        var ui = new TestInteraction();
        ui.OnProgress = p => {
            if (p.Stage != Stage.Zipping || substituted != null) return;
            substituted = p.Path == Path.GetFileName(first) ? second : first;
            File.Delete(substituted);
            CreateTestFileSymlink(substituted, secret);
        };
        try
        {
            using (new OperationContext(ui, CancellationToken.None).Activate())
                Throws(() => ZipBackup(backup), "ZIP rechecks metadata immediately before reading each entry");
            Assert(substituted != null && Directory.GetFiles(root, "*.zip*").Length == 0,
                "metadata substituted during export aborts and removes the incomplete archive");
            Assert(File.ReadAllText(secret) == "AUDIT-SECRET", "ZIP link rejection preserves outside content");
        }
        finally { if (substituted != null) File.Delete(substituted); }
    }
}
