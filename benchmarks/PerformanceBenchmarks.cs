using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

static class PerformanceBenchmarks
{
    sealed class Quiet : IOperationInteraction
    {
        public void Write(string text) { }
        public void WriteLine(string text) { }
        public void WriteColored(string text, ConsoleColor color) { }
        public char Choose(string question, IList<Option> options) { return options[0].Key; }
        public int ChooseItem(string question, IList<string> items, string noneLabel) { return 1; }
        public string AskText(string question) { return null; }
        public bool ReviewPlan(OperationPlan plan) { return !plan.PreviewOnly; }
        public void ReportProgress(OperationProgress progress) { }
    }

    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        int iterations = int.Parse(args[1]);
        string result = args[2];
        Directory.CreateDirectory(root);
        AppDomain.MonitoringIsEnabled = true;
        var rows = new List<string> { "Scenario,Iteration,Milliseconds,AllocatedBytes,Gen2Collections" };
        try
        {
            string small = Path.Combine(root, "small"), large = Path.Combine(root, "large");
            Directory.CreateDirectory(small);
            Directory.CreateDirectory(large);
            for (int i = 0; i < 300; i++) File.WriteAllBytes(Path.Combine(small, i + ".wav"), new byte[4096]);
            using (var file = File.Create(Path.Combine(large, "large.wav")))
            {
                var buffer = new byte[1 << 20];
                for (int i = 0; i < 64; i++) file.Write(buffer, 0, buffer.Length);
            }
            var config = new Config { BackupFolder = Path.Combine(root, "catalog"), SettingsDirectory = root, Preview = false, MaxNameLength = 0 };
            string history = Path.Combine(config.BackupFolder, AppIdentity.BackupsFolderName, "profiles", "bench");
            for (int i = 1; i <= 12; i++)
            {
                string backup = Path.Combine(history, "2026-01-" + i.ToString("D2"));
                Directory.CreateDirectory(backup);
                Workflow.AtomicText(Path.Combine(backup, AppIdentity.MetadataFolderName, "status.txt"), Status.Complete + "\n");
                for (int f = 0; f < 200; f++) File.WriteAllBytes(Path.Combine(backup, f + ".wav"), new byte[32]);
            }
            Action dashboard = () => {
                MethodInfo method = typeof(StorageCatalog).GetMethod("ScanDashboardCatalog", BindingFlags.Static | BindingFlags.NonPublic);
                if (method == null) StorageCatalog.ScanBackupCatalog(config, Path.Combine(root, "no-mirrors"));
                else method.Invoke(null, new object[] { config });
            };
            Measure(rows, "Dashboard-large-history", iterations, dashboard);
            Measure(rows, "Storage-large-history", iterations, () => StorageCatalog.ScanBackupCatalog(config, Path.Combine(root, "no-mirrors")));
            Measure(rows, "Small-copy-and-hash-memory", iterations, () => {
                var data = new byte[4096];
                for (int i = 0; i < 300; i++)
                    using (var input = new MemoryStream(data)) using (var output = new MemoryStream())
                        FileCopy.CopyAndHash(input, output, "memory");
            });
            RunSource(rows, "Many-small-files", small, root, config, iterations);
            RunSource(rows, "Large-WAV", large, root, config, iterations);
            File.WriteAllLines(result, rows);
            Console.WriteLine("Benchmark CSV: " + result);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void RunSource(List<string> rows, string name, string source, string root, Config config, int iterations)
    {
        int run = 0;
        string history = null, previous = null;
        Measure(rows, name + "-full", iterations, () => {
            history = Path.Combine(root, name + "-" + run++);
            OperationResult result = Backups.RunBackup(source, history, null, false, config);
            if (result.ExitCode != 0) throw new IOException(result.Summary);
            previous = result.OutputFolder;
        });
        Measure(rows, name + "-unchanged", iterations, () => {
            OperationResult result = Backups.RunBackup(source, history, previous, true, config);
            if (result.ExitCode != 0) throw new IOException(result.Summary);
        });
        Measure(rows, name + "-verify", iterations, () => {
            foreach (string file in Directory.GetFiles(source)) FileCopy.HashFileFromDisk(file);
        });
    }

    static void Measure(List<string> rows, string name, int iterations, Action action)
    {
        for (int i = -1; i < iterations; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            int gen2 = GC.CollectionCount(2);
            var timer = Stopwatch.StartNew();
            using (new OperationContext(new Quiet(), CancellationToken.None).Activate()) action();
            timer.Stop();
            long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
            if (i < 0) continue; // one warm-up per scenario
            string row = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F3},{3},{4}", name, i + 1, timer.Elapsed.TotalMilliseconds, allocated, GC.CollectionCount(2) - gen2);
            rows.Add(row);
            Console.WriteLine(row);
        }
    }
}
