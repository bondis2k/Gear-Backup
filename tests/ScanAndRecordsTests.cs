using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using GearBackup;

// The small foundation helpers that guard untrusted input: the source scan, the bounded record reader, the free-space check.
partial class Tests
{
    static void MakeJunction(string link, string target)
    {
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        mklink.WaitForExit();
    }

    [Test("Source scan skips other computers' leftovers")]
    static void TestSourceScanLeftovers(string root)
    {
        foreach (string name in new[] { ".DS_Store", "._song", "notes.txt~", "FSCK0007.REC", "thumbs.db", "EHTHUMBS.DB", "Desktop.ini" })
            Assert(SourceScan.IsLeftover(name, false), name + " is a leftover file");
        foreach (string name in new[] { "song.m8s", "FSCK7.REC", "FSCK00001.REC", "a.txt" })
            Assert(!SourceScan.IsLeftover(name, false), name + " is a normal file");
        Assert(SourceScan.IsLeftover("lost+found", true) && SourceScan.IsLeftover(".Trashes", true) && !SourceScan.IsLeftover("Songs", true), "folder leftovers are recognised");
        Assert(!SourceScan.IsLeftover("lost+found", false), "lost+found is a leftover only as a folder");

        Put(Path.Combine(root, "Songs", "a.m8s"), "song");
        Put(Path.Combine(root, "Songs", "deep", "b.m8s"), "song");
        Put(Path.Combine(root, ".DS_Store"), "12345");
        Put(Path.Combine(root, "Thumbs.db"), "123");
        Put(Path.Combine(root, "lost+found", "x.bin"), "123456");
        Put(Path.Combine(root, "lost+found", "inner", "y.bin"), "12");
        Put(Path.Combine(root, ".Trashes", "z.bin"), "1");
        Put(Path.Combine(root, "System Volume Information", "w.bin"), "1");
        Put(Path.Combine(root, "$RECYCLE.BIN", "v.bin"), "1");
        var scan = new ScanResult();
        SourceScan.CollectFiles(root, scan);
        Assert(scan.Files.Count == 2 && scan.Folders.Count == 2, "only the real files and folders are collected: " + scan.Files.Count + "/" + scan.Folders.Count);
        Assert(scan.SkippedFiles == 2 && scan.SkippedFileBytes == 8, "leftover files are counted with their size");
        Assert(scan.SkippedFolders == 2 && scan.SkippedFolderFiles == 3 && scan.SkippedFolderBytes == 9, "leftover folders are counted with what is inside them: " + scan.SkippedFolders + "/" + scan.SkippedFolderFiles + "/" + scan.SkippedFolderBytes);
        Assert(scan.SkippedNamed.Count == 2 && scan.SkippedNamed.Exists(n => n.EndsWith("Thumbs.db")) && scan.SkippedNamed.Exists(n => n.EndsWith("lost+found\\")),
            "leftovers that do not start with a dot are listed by name: " + string.Join("|", scan.SkippedNamed));

        string linked = Folder(root, "linked-source");
        Directory.CreateDirectory(Path.Combine(linked, "real"));
        Directory.CreateDirectory(Path.Combine(root, "elsewhere"));
        MakeJunction(Path.Combine(linked, "junction"), Path.Combine(root, "elsewhere"));
        if (Directory.Exists(Path.Combine(linked, "junction")))
        {
            Throws(() => SourceScan.CollectFiles(linked, new ScanResult()), "a linked folder in the source is refused");
            Directory.Delete(Path.Combine(linked, "junction"));   // removes only the junction
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool stopped = false;
            using (new OperationContext(new TestInteraction(), cancelled.Token).Activate())
                try { SourceScan.CollectFiles(root, new ScanResult()); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "a cancelled scan stops");
        }
    }

    [Test("Bounded record reader and free-space check")]
    static void TestRecordsAndDiskSpace(string root)
    {
        string file = Path.Combine(root, "records", "list.txt");
        Put(file, "one\ntwo\nthree");
        Assert(string.Join(",", Records.ReadLines(file, Records.SmallLimit, "List")) == "one,two,three", "lines are read");
        Assert(Records.ReadText(file, Records.SmallLimit, "List").StartsWith("one") && Records.ReadBytes(file, Records.SmallLimit, "List").Length == 13, "text and bytes are read");
        Assert(Records.ReadText(file, 13, "List").Length == 13, "a file exactly at the limit is read");
        Throws(() => Records.ReadText(file, 12, "List"), "a file over the limit is refused");
        Throws(() => Records.ReadBytes(file, 5, "List"), "bytes over the limit are refused");
        Throws(() => Records.ReadLines(Path.Combine(root, "records", "missing.txt"), 100, "List"), "a missing record is an error");

        using (var stream = new LimitedReadStream(new MemoryStream(new byte[10]), 4, "too big"))
        {
            Assert(stream.CanRead && !stream.CanSeek && !stream.CanWrite, "the limited stream only reads");
            Throws(() => { var buffer = new byte[16]; while (stream.Read(buffer, 0, buffer.Length) > 0) { } }, "reading past the budget fails");
            Assert(stream.Position == 0 || stream.Position <= 4, "the position counts the bytes read within the budget");
            Throws2<NotSupportedException>(() => { long length = stream.Length; }, "no length");
            Throws2<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin), "no seek");
            Throws2<NotSupportedException>(() => stream.SetLength(1), "no resize");
            Throws2<NotSupportedException>(() => stream.Write(new byte[1], 0, 1), "no write");
            Throws2<NotSupportedException>(() => stream.Position = 0, "no position change");
            stream.Flush();
        }
        Throws2<ArgumentOutOfRangeException>(() => new LimitedReadStream(new MemoryStream(), -1, "x"), "a negative limit is refused");

        Assert(DiskSpace.Shortage(root, 1, "the test") == null, "a small need fits");
        string shortage = DiskSpace.Shortage(root, long.MaxValue, "the test");
        Assert(shortage != null && shortage.Contains("Not enough space for the test") && shortage.Contains("is free"), "an impossible need is described: " + shortage);
        Throws(() => DiskSpace.Shortage("\0bad", 1, "the test"), "a path that cannot be resolved is reported");

        bool preview = PlanReview.PreviewOnly;
        try
        {
            PlanReview.PreviewOnly = true;
            Assert(PlanReview.PreviewOnly, "preview-only can be switched on");
            PlanReview.PreviewOnly = false;
            Assert(!PlanReview.PreviewOnly, "and off");
        }
        finally { PlanReview.PreviewOnly = preview; }
    }

    [Test("M8 card check skips linked folders")]
    static void TestM8CardCheckLinks(string root)
    {
        Put(Path.Combine(root, "Songs", "good.m8s"), "song");
        Put(Path.Combine(root, "Samples", "kick.wav"), "sample");
        Put(Path.Combine(root, "outside", "other.m8s"), "song");
        MakeJunction(Path.Combine(root, "Linked"), Path.Combine(root, "outside"));
        if (!Directory.Exists(Path.Combine(root, "Linked"))) return;       // links cannot be made here
        try
        {
            M8CardReport report = M8CardCheck.Run(root, new CardTestReader());
            Assert(report.Warnings.Exists(w => w.StartsWith("Skipped linked path") && w.EndsWith("Linked")), "a linked folder is skipped with a warning: " + string.Join("|", report.Warnings));
            Assert(!report.Songs.Exists(s => s.Relative.Contains("Linked")), "songs behind the link are not read");
            Throws(() => M8CardCheck.Run(Path.Combine(root, "Linked"), new CardTestReader()), "a card root that is itself a link is refused");
            Throws(() => M8CardCheck.Run(" ", new CardTestReader()), "a blank card root is refused");
            Throws(() => M8CardCheck.Run(Path.Combine(root, "no-such-card"), new CardTestReader()), "a missing card root is refused");
        }
        finally { Directory.Delete(Path.Combine(root, "Linked")); }      // removes only the junction
    }

    static void Throws2<T>(Action action, string message) where T : Exception
    {
        bool threw = false;
        try { action(); } catch (T) { threw = true; }
        Assert(threw, message);
    }
}
