using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using GearBackup;
using GearBackup.Cli;

// Marks a method of the Tests class as a test. The runners find these by reflection, so adding a test is writing the
// method with this attribute; there is no list to keep.
//
//   [Test("Zip failures", Folder = "zip")] static void TestZipFailures(string dir) { ... }
//
// A test method takes no argument, or one string: a fixture folder made fresh for the run.
[AttributeUsage(AttributeTargets.Method)]
sealed class TestAttribute : Attribute
{
    public readonly string Name;
    // The fixture folder under the run's root: "x", "helpers/x" for a nested one, "." for the root itself. Tests that name
    // the same folder share it. Without it a test that takes a folder gets one named after its method.
    public string Folder;
    // GUI tests run in the GUI test program (on an STA thread, with no operation context).
    public bool Gui;
    public TestAttribute(string name) { Name = name; }
}

// Finding, filtering and running the tests. Both test programs (core and GUI) start here.
//
//   GearBackup.Tests.exe                      run everything
//   GearBackup.Tests.exe --filter zip         only tests whose name (or method name) contains "zip"; repeat for more
//   GearBackup.Tests.exe --list               list the tests
//
// tests\run-tests.ps1 and tests\run-gui-tests.ps1 pass these on as -Filter and -List.
partial class Tests
{
    sealed class TestCase
    {
        public string Name, Folder;
        public MethodInfo Method;
    }

    static List<TestCase> Discover(bool gui)
    {
        var found = new List<TestCase>();
        foreach (MethodInfo method in typeof(Tests).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var mark = (TestAttribute)Attribute.GetCustomAttribute(method, typeof(TestAttribute));
            if (mark == null || mark.Gui != gui) continue;
            if (method.GetParameters().Length > 1 || (method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType != typeof(string)))
                throw new InvalidOperationException("Test " + method.Name + " must take no argument or one string (a folder).");
            found.Add(new TestCase { Name = mark.Name, Folder = mark.Folder ?? method.Name, Method = method });
        }
        // Declaration order, so a run is the same every time.
        found.Sort((a, b) => a.Method.MetadataToken.CompareTo(b.Method.MetadataToken));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TestCase test in found)
            if (!names.Add(test.Name)) throw new InvalidOperationException("Two tests are named \"" + test.Name + "\".");
        return found;
    }

    static bool Matches(TestCase test, List<string> filters)
    {
        if (filters.Count == 0) return true;
        foreach (string filter in filters)
            if (test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                test.Method.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // Runs the tests selected by the command line and returns the exit code.
    internal static int RunSelected(string[] args, bool gui)
    {
        var filters = new List<string>();
        bool list = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) filters.Add(args[++i]);
            else if (args[i] == "--list") list = true;
            else
            {
                Console.Error.WriteLine("Unknown argument: " + args[i] + ". Use --filter TEXT (repeatable) or --list.");
                return 2;
            }
        }
        List<TestCase> tests = Discover(gui).FindAll(test => Matches(test, filters));
        if (list)
        {
            foreach (TestCase test in tests) Console.WriteLine(test.Name + "  [" + test.Method.Name + "]");
            Console.WriteLine(tests.Count + " test(s).");
            return 0;
        }
        if (tests.Count == 0)
        {
            Console.Error.WriteLine("No test matches " + string.Join(", ", filters) + ". Use --list to see them.");
            return 1;
        }
        string root = Path.Combine(Environment.CurrentDirectory, ".test-runs", (gui ? "gui-" : "") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (TestCase test in tests)
        {
            TestCase current = test;
            Action run = () => Invoke(current, root);
            if (gui) RunGuiTest(current.Name, run);
            else RunTest(current.Name, run);
        }
        return Summarize(root);
    }

    static void Invoke(TestCase test, string root)
    {
        object[] arguments = test.Method.GetParameters().Length == 0 ? new object[0]
            : new object[] { test.Folder == "." ? Folder(root, "") : Folder(root, test.Folder.Replace('/', '\\')) };
        try { test.Method.Invoke(null, arguments); }
        catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
    }

    // A GUI test: like RunTest, but window code gets no operation context (it starts its own where it needs one).
    static void RunGuiTest(string name, Action test)
    {
        Record(name, test, null);
    }
}
