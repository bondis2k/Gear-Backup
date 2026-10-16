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
                new Config { Preview = true }, new OperationContext(ui, CancellationToken.None)),
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

    [Test("Security: ZIP metadata symbolic links", Folder = "helpers")]
    static void TestZipMetadataLinks(string dir)
    {
        string root = Folder(dir, "zip-metadata-links"), source = Folder(root, "source"), history = Path.Combine(root, "history");
        string secret = Path.Combine(root, "secret.txt"); Put(secret, "AUDIT-SECRET");
        Put(Path.Combine(source, "a.txt"), "FIRST"); Put(Path.Combine(source, "b.txt"), "SECOND");
        string backup = RunBackup(source, history, null, false, new Config { Preview = false }).OutputFolder;
        var entries = ContentStoreBackup.Current(backup).Files;
        string objectA = entries.Find(e => e.Relative == "a.txt").StoredPath, objectB = entries.Find(e => e.Relative == "b.txt").StoredPath;
        byte[] savedA = File.ReadAllBytes(objectA), savedB = File.ReadAllBytes(objectB);

        // A stored object that is a link is never followed.
        File.Delete(objectA);
        CreateTestFileSymlink(objectA, secret);
        try
        {
            Throws(() => WithUi(() => ZipBackup(backup)), "ZIP rejects a linked stored object");
            Assert(Directory.GetFiles(history, "*.zip*").Length == 0, "a linked stored object leaves no archive or staging file");
        }
        finally { File.Delete(objectA); File.WriteAllBytes(objectA, savedA); }

        // An object can change after the export started, too.
        string substituted = null;
        var ui = new TestInteraction();
        ui.OnProgress = p => {
            if (p.Stage != Stage.Zipping || substituted != null) return;
            substituted = p.Path == "a.txt" ? objectB : objectA;
            File.Delete(substituted);
            CreateTestFileSymlink(substituted, secret);
        };
        try
        {
            using (new OperationContext(ui, CancellationToken.None).Activate())
                Throws(() => ZipBackup(backup), "ZIP rechecks each stored object immediately before reading it");
            Assert(substituted != null && Directory.GetFiles(history, "*.zip*").Length == 0,
                "an object substituted during export aborts and removes the incomplete archive");
            Assert(File.ReadAllText(secret) == "AUDIT-SECRET", "ZIP link rejection preserves outside content");
        }
        finally { if (substituted != null) { File.Delete(substituted); File.WriteAllBytes(substituted, substituted == objectA ? savedA : savedB); } }
    }
}
