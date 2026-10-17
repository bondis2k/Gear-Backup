using System;
using System.IO;

partial class Tests
{
    [Test("Fixture cleanup: links, read-only files and boundaries", Folder = "fixture-cleanup")]
    static void TestFixtureCleanup(string root)
    {
        string tree = Folder(root, "tree"), target = Folder(root, "target");
        Put(Path.Combine(target, "child", "keep.txt"), "KEEP");
        Put(Path.Combine(tree, "nested", "readonly.txt"), "REMOVE");
        File.SetAttributes(Path.Combine(tree, "nested", "readonly.txt"), FileAttributes.ReadOnly);
        string link = Path.Combine(tree, "linked");
        CreateTestJunction(link, target);
        Throws(() => FixtureCleanup.Delete(Path.Combine(link, "child")), "cleanup refuses a linked ancestor");
        FixtureCleanup.Delete(tree);
        Assert(!Directory.Exists(tree) && File.ReadAllText(Path.Combine(target, "child", "keep.txt")) == "KEEP",
            "cleanup removes the junction and read-only fixture, preserving the linked target");
        tree = Folder(root, "dangling");
        string gone = Folder(root, "gone");
        CreateTestJunction(Path.Combine(tree, "link"), gone);
        Directory.Delete(gone);
        FixtureCleanup.Delete(tree);
        Assert(!Directory.Exists(tree), "dangling junctions can also be removed");
        string external = Path.Combine(Path.GetTempPath(), "gear-fixture-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        string sentinel = Path.Combine(external, "keep.txt");
        try
        {
            File.WriteAllText(sentinel, "KEEP");
            Throws(() => FixtureCleanup.Delete(external), "cleanup refuses paths outside .test-runs");
            Assert(File.ReadAllText(sentinel) == "KEEP", "refused paths remain untouched");
        }
        finally { File.Delete(sentinel); Directory.Delete(external); }
    }

    [Test("Fixture cleanup: hard-link attributes remain protected", Folder = "fixture-hardlinks")]
    static void TestFixtureHardLinks(string root)
    {
        if (!SupportsHardLinks(root)) throw new TestSkippedException("Hard links require NTFS.");
        string tree = Folder(root, "tree"), outside = Path.Combine(root, "keep.txt");
        Put(outside, "KEEP");
        Assert(TryHardLink(outside, Path.Combine(tree, "shared.txt")), "hard-link fixture created");
        File.SetAttributes(outside, FileAttributes.ReadOnly);
        try
        {
            Throws(() => FixtureCleanup.Delete(tree), "cleanup refuses to clear shared read-only attributes");
            Assert((File.GetAttributes(outside) & FileAttributes.ReadOnly) != 0 && File.ReadAllText(outside) == "KEEP",
                "shared file contents and attributes remain unchanged");
        }
        finally { File.SetAttributes(outside, FileAttributes.Normal); }
        FixtureCleanup.Delete(tree);
        Assert(File.ReadAllText(outside) == "KEEP", "a writable hard-link name can be removed without deleting its other name");
    }
}
