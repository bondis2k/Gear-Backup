// Console questions, progress line and formatting helpers.
using System;
using System.Text;
using System.Threading;

partial class M8Backup
{
    static bool AskInConsole(string question)
    {
        return AskKey(question, "yn") == 'y';
    }

    // Waits for one of the allowed keys (case-insensitive) and returns it in lower case.
    static char AskKey(string question, string allowed)
    {
        OperationContext.Current.CheckCancellation();
        char answer = OperationContext.Current.Interaction.ChooseKey(question, allowed);
        OperationContext.Current.CheckCancellation();
        return answer;
    }

    static int AskNumber(string question, int max)
    {
        OperationContext.Current.CheckCancellation();
        int answer = OperationContext.Current.Interaction.ChooseNumber(question, max);
        OperationContext.Current.CheckCancellation();
        return answer;
    }

    sealed class ConsoleInteraction : IOperationInteraction
    {
        string lastStage;
        DateTime lastProgress;
        public void Write(string text) { Console.Write(text); }
        public void WriteLine(string text) { Console.WriteLine(text); }
        // Reads key by key rather than Console.ReadLine, so Ctrl+C can cancel while waiting for input.
        // Redirected input (tests, scripts) is read as plain lines.
        public string ReadLine()
        {
            if (Console.IsInputRedirected || Console.In is System.IO.StringReader) return Console.ReadLine();
            var text = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = ReadKey();
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
                if (key.Key == ConsoleKey.Backspace && text.Length > 0)
                {
                    text.Length--;
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar)) { text.Append(key.KeyChar); Console.Write(key.KeyChar); }
            }
        }
        ConsoleKeyInfo ReadKey()
        {
            while (!Console.KeyAvailable)
            {
                OperationContext.Current.CheckCancellation();
                Thread.Sleep(50);
            }
            OperationContext.Current.CheckCancellation();
            return Console.ReadKey(true);
        }
        public void WriteColored(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
        // Rewrites one status line in place, at most every 150 ms unless the stage changes or a file finishes.
        public void ReportProgress(OperationProgress progress)
        {
            if (Console.IsOutputRedirected) return;
            if (progress.OverallOnly && (progress.Stage == "Copying" || progress.Stage == "Hashing" || progress.Stage == "Verifying" ||
                progress.Stage == "Comparing" || progress.Stage == "Scanning backup")) return;
            DateTime now = DateTime.UtcNow;
            bool changed = progress.Stage != lastStage;
            bool finished = progress.TotalBytes > 0 && progress.Bytes == progress.TotalBytes;
            if (!changed && (now - lastProgress).TotalMilliseconds < 150 && !finished) return;
            if (changed) Console.WriteLine();
            lastStage = progress.Stage;
            lastProgress = now;
            if (progress.Stage == "Backup copy")
            {
                int percent = progress.TotalBytes == 0 ? 100 : (int)(progress.Bytes * 100 / progress.TotalBytes);
                Console.Write("\r  {0,3}%  {1}/{2} files processed      ", percent, progress.Files, progress.TotalFiles);
            }
            else if (progress.OverallOnly) Console.Write("\r  {0}...      ", progress.Stage);
            else Console.Write("\r  {0}: {1}{2}      ", progress.Stage, progress.Path,
                progress.TotalBytes > 0 ? " (" + FormatSize(progress.Bytes) + " / " + FormatSize(progress.TotalBytes) + ")" : "");
        }

        public bool ReviewPlan(OperationPlan plan)
        {
            Console.WriteLine("  {0}: {1:N0} action(s), {2} to copy.", plan.Label, plan.Actions.Count, FormatSize(plan.Bytes));
            if (plan.ShowDetails || plan.PreviewOnly)
                foreach (string line in plan.Actions) Console.WriteLine("    " + line);
            if (plan.PreviewOnly) { Console.WriteLine("  Preview only; no files changed."); return false; }
            return !plan.ShowDetails || ChooseKey("  Proceed? (y/n) ", "yn") == 'y';
        }

        public char ChooseKey(string question, string allowed)
        {
            Console.Write(question);
            while (true)
            {
                char c;
                if (Console.IsInputRedirected || Console.In is System.IO.StringReader)
                {
                    string line = Console.ReadLine();
                    if (line == null) throw new OperationCanceledException("Input ended; operation cancelled.");
                    if (line.Trim().Length == 0) continue;
                    c = char.ToLowerInvariant(line.Trim()[0]);
                }
                else c = char.ToLowerInvariant(ReadKey().KeyChar);
                if (allowed.IndexOf(c) >= 0) { Console.WriteLine(c); return c; }
            }
        }

        // Reads a number from 1 to max; returns 0 if the user just presses Enter.
        public int ChooseNumber(string question, int max)
        {
            while (true)
            {
                Console.Write(question);
                string line = ReadLine();
                if (line == null || line.Trim().Length == 0) return 0;
                int n;
                if (int.TryParse(line.Trim(), out n) && n >= 1 && n <= max) return n;
                Console.WriteLine("  Please enter a number from 1 to " + max + ".");
            }
        }

    }

    static void PrintProgress(long processedBytes, long totalBytes, int done, int total, long copiedBytes)
    {
        OperationContext.Current.Progress("Backup copy", "", processedBytes, totalBytes, done, total);
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? bytes + " B" : string.Format("{0:N1} {1}", size, units[unit]);
    }

    static string FormatTime(TimeSpan t)
    {
        if (t.TotalMinutes >= 1) return string.Format("{0}m {1}s", (int)t.TotalMinutes, t.Seconds);
        return string.Format("{0:N1}s", t.TotalSeconds);
    }

    static void WriteColored(string text, ConsoleColor color)
    {
        OperationContext.Current.Interaction.WriteColored(text, color);
    }
}
