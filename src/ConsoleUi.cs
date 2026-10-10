// Console questions, progress line and formatting helpers.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

partial class GearBackup
{
    static readonly Option Yes = new Option('y', "Yes"), No = new Option('n', "No");

    // Asks a question with a fixed set of answers; returns the chosen option's key.
    static char Ask(string question, params Option[] options)
    {
        OperationContext.Current.CheckCancellation();
        char answer = OperationContext.Current.Interaction.Choose(question, options);
        OperationContext.Current.CheckCancellation();
        return answer;
    }

    static bool AskYesNo(string question)
    {
        return Ask(question, Yes, No) == 'y';
    }

    // Lets the user pick one of `items`; 1..items.Count, or 0 for noneLabel / Enter (see ChooseItem).
    static int AskItem(string question, IList<string> items, string noneLabel = null)
    {
        OperationContext.Current.CheckCancellation();
        int answer = OperationContext.Current.Interaction.ChooseItem(question, items, noneLabel);
        OperationContext.Current.CheckCancellation();
        return answer;
    }

    // Free text such as a search or a path; null when cancelled.
    static string AskText(string question)
    {
        OperationContext.Current.CheckCancellation();
        string answer = OperationContext.Current.Interaction.AskText(question);
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
        string ReadLine()
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
            if (progress.OverallOnly && Stage.IsFileDetail(progress.Stage)) return;
            DateTime now = DateTime.UtcNow;
            bool changed = progress.Stage != lastStage;
            bool finished = progress.TotalBytes > 0 && progress.Bytes == progress.TotalBytes;
            if (!changed && (now - lastProgress).TotalMilliseconds < 150 && !finished) return;
            if (changed) Console.WriteLine();
            lastStage = progress.Stage;
            lastProgress = now;
            if (progress.Stage == Stage.BackupCopy)
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
            return !plan.ShowDetails || Choose("Proceed?", new[] { Yes, No }) == 'y';
        }

        // Yes/no questions end in "(y/n)". When every label contains its key's letter, the options are shown
        // inline ("[R]eplace them, [K]eep them or [C]ancel"); otherwise as a numbered menu ("    1) Full backup").
        public char Choose(string question, IList<Option> options)
        {
            var keys = new StringBuilder();
            foreach (Option option in options) keys.Append(char.ToLowerInvariant(option.Key));
            string allowed = keys.ToString();
            bool inline = true;
            foreach (Option option in options)
                if (option.Label.IndexOf(option.Key.ToString(), StringComparison.OrdinalIgnoreCase) < 0) inline = false;

            if (allowed == "yn") Console.Write("  " + question + " (y/n) ");
            else if (inline)
            {
                var labels = new List<string>();
                foreach (Option option in options) labels.Add(Bracket(option));
                Console.Write("  " + question + " " + JoinOr(labels) + "? ");
            }
            else
            {
                foreach (Option option in options) Console.WriteLine("    {0}) {1}", option.Key, option.Label);
                Console.Write("  " + question + " (" + JoinOr(new List<string>(Array.ConvertAll(allowed.ToCharArray(), c => c.ToString()))) + ") ");
            }
            return ReadKeyFrom(allowed);
        }

        // "Replace them" with key 'r' -> "[R]eplace them".
        static string Bracket(Option option)
        {
            int i = option.Label.IndexOf(option.Key.ToString(), StringComparison.OrdinalIgnoreCase);
            return option.Label.Substring(0, i) + "[" + char.ToUpperInvariant(option.Label[i]) + "]" + option.Label.Substring(i + 1);
        }

        // "a, b or c"
        static string JoinOr(List<string> items)
        {
            if (items.Count < 2) return string.Join("", items.ToArray());
            return string.Join(", ", items.GetRange(0, items.Count - 1).ToArray()) + " or " + items[items.Count - 1];
        }

        char ReadKeyFrom(string allowed)
        {
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

        // Prints the numbered list and reads a number; Enter returns 0.
        public int ChooseItem(string question, IList<string> items, string noneLabel)
        {
            for (int i = 0; i < items.Count; i++) Console.WriteLine("    {0,2}) {1}", i + 1, items[i]);
            string enter = noneLabel == null ? "Enter to cancel" : "Enter for " + noneLabel.ToLowerInvariant();
            while (true)
            {
                Console.Write("  " + question + " Number, or " + enter + ": ");
                string line = ReadLine();
                if (line == null || line.Trim().Length == 0) return 0;
                int n;
                if (int.TryParse(line.Trim(), out n) && n >= 1 && n <= items.Count) return n;
                Console.WriteLine("  Please enter a number from 1 to " + items.Count + ".");
            }
        }

        public string AskText(string question)
        {
            Console.Write("  " + question + " ");
            return ReadLine();
        }

    }

    static void PrintProgress(long processedBytes, long totalBytes, int done, int total, long copiedBytes)
    {
        OperationContext.Current.Progress(Stage.BackupCopy, "", processedBytes, totalBytes, done, total);
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
