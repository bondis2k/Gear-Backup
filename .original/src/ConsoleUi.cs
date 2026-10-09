// Console questions, progress line and formatting helpers.
using System;

partial class M8Backup
{
    static bool AskInConsole(string question)
    {
        return AskKey(question, "yn") == 'y';
    }

    // Waits for one of the allowed keys (case-insensitive) and returns it in lower case.
    static char AskKey(string question, string allowed)
    {
        Console.Write(question);
        while (true)
        {
            char c = char.ToLowerInvariant(Console.ReadKey(true).KeyChar);
            if (allowed.IndexOf(c) >= 0) { Console.WriteLine(c); return c; }
        }
    }

    // Reads a number from 1 to max; returns 0 if the user just presses Enter.
    static int AskNumber(string question, int max)
    {
        while (true)
        {
            Console.Write(question);
            string line = Console.ReadLine();
            if (line == null || line.Trim().Length == 0) return 0;
            int n;
            if (int.TryParse(line.Trim(), out n) && n >= 1 && n <= max) return n;
            Console.WriteLine("  Please enter a number from 1 to " + max + ".");
        }
    }

    static void PrintProgress(long processedBytes, long totalBytes, int done, int total, long copiedBytes)
    {
        int percent = totalBytes == 0 ? 100 : (int)(processedBytes * 100 / totalBytes);
        Console.Write("\r  {0,3}%  {1}/{2} files  {3} copied      ", percent, done, total, FormatSize(copiedBytes));
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
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
