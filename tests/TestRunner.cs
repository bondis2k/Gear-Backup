using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using static GearBackup.PathSafety;
using static GearBackup.Snapshots;
using static GearBackup.Backups;
using static GearBackup.RestoreFlow;
using static GearBackup.ConsoleUi;
using static GearBackup.BackupFolders;
using GearBackup;
using GearBackup.Cli;

class TestRunner
{
    static int Main(string[] args)
    {
        try { return Tests.RunSelected(args, false); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

partial class Tests
{
    static int assertions;
    static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("ASSERTION FAILED: " + message);
    }
    // Fixtures are kept after a failure (Summarize skips this) or when GEAR_KEEP_TEST_FIXTURES=1.
    static void RemoveFixtures(string root)
    {
        if (Environment.GetEnvironmentVariable("GEAR_KEEP_TEST_FIXTURES") == "1")
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
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
    // A second name for an existing file, as a hostile or odd file system layout could present one.
    static bool TryHardLink(string existing, string link)
    {
        try { return CreateHardLink(link, existing, IntPtr.Zero); }
        catch (Exception) { return false; }
    }
    static bool SupportsHardLinks(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).DriveFormat == "NTFS"; }
        catch (Exception) { return false; }
    }
    static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
    static Snapshot LatestSnapshot(string backup)
    {
        return ReadSnapshot(backup, RestorePointNames.OrderedManifests(backup)[0]);
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
    static int skippedTests;
    sealed class TestSkippedException : Exception
    {
        public TestSkippedException(string reason) : base(reason) { }
    }

    // Runs test code that calls core helpers directly, as an operation would. GUI tests use it for
    // their own setup; window code gets no such context, so a window that needs one fails there.
    static T InOperation<T>(Func<T> action)
    {
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate()) return action();
    }

    // Runs one test. A failure is recorded and the next test still runs. Each test runs in its own
    // operation context (console prompts and output, as the CLI has), so preview mode, the device and
    // caches start fresh; console input is reset afterwards so it can't leak into the next test.
    static void RunTest(string name, Action test)
    {
        Record(name, test, new ConsoleInteraction());
    }

    // Runs `test` (in an operation context with this interaction, or in none when it is null), and records ok, skipped
    // or failed with how long it took.
    static void Record(string name, Action test, IOperationInteraction interaction)
    {
        int before = assertions;
        TextReader input = Console.In;
        var timer = Stopwatch.StartNew();
        try
        {
            if (interaction == null) test();
            else using (new OperationContext(interaction, CancellationToken.None).Activate()) test();
            results.Add(string.Format("  ok    {0} ({1} checks, {2} ms)", name, assertions - before, timer.ElapsedMilliseconds));
        }
        catch (TestSkippedException ex)
        {
            skippedTests++;
            results.Add("  SKIP  " + name + "\n          " + ex.Message);
        }
        catch (Exception ex)
        {
            failedTests++;
            string reason = ex.Message.StartsWith("ASSERTION FAILED") ? ex.Message : ex.GetType().Name + ": " + ex.Message;
            // The first stack line outside the Assert/Throws helpers is the check that failed.
            string where = "";
            foreach (string frame in (ex.StackTrace ?? "").Split('\n'))
                if (!frame.Contains(".Assert(") && !frame.Contains(".Throws(")) { where = frame.Trim(); break; }
            // An unexpected exception (not a failed check) shows more of its stack.
            if (!ex.Message.StartsWith("ASSERTION FAILED"))
                where = string.Join("\n          ", (ex.StackTrace ?? "").Split((char)10).Take(8).Select(frame => frame.Trim()).ToArray());
            results.Add(string.Format("  FAIL  {0}\n          {1}\n          {2}", name, reason, where));
        }
        finally { Console.SetIn(input); }
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
            Console.WriteLine("PASS: " + (results.Count - skippedTests) + " tests, " + assertions + " assertions." +
                (skippedTests > 0 ? " " + skippedTests + " test(s) skipped; see reasons above." : ""));
            RemoveFixtures(root);
            return 0;
        }
        Console.WriteLine("FAIL: " + failedTests + " of " + results.Count + " tests failed. Fixtures kept: " + root);
        return 1;
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

    sealed class TestInteraction : IOperationInteraction
    {
        public readonly StringBuilder Output = new StringBuilder();
        public readonly List<OperationProgress> Progress = new List<OperationProgress>();
        public Action<OperationProgress> OnProgress;
        public Action<OperationPlan> OnReview;
        public int Plans;
        public void Write(string text) { Output.Append(text); }
        public void WriteLine(string text) { Output.AppendLine(text); }
        public void WriteColored(string text, ConsoleColor color) { WriteLine(text); }
        public Func<string, string> OnAskText;
        public string AskText(string question) { if (OnAskText != null) return OnAskText(question); throw new Exception("Engine unexpectedly asked for text: " + question); }
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
        public bool ReviewPlan(OperationPlan plan) { Plans++; if (OnReview != null) OnReview(plan); return !plan.PreviewOnly; }
        public void ReportProgress(OperationProgress progress)
        {
            // Copy workers report from several threads at once.
            lock (Progress) Progress.Add(progress);
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
}

// Tests leave out Notifications.cs, whose Windows notifications would write to the registry and appear on
// the desktop; these stand-ins replace it.
namespace GearBackup.Cli
{
    static class Notifications
    {
        internal static bool? ShowToast(string title, string summary, bool ask) { return false; }
        internal static void Finish(string pnp, string title, string summary, EjectMode mode) { }
    }
}
