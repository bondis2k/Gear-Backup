using System;

namespace GearBackup
{
    // Sizes and durations as the console, reports and windows show them.
    static class Formatting
    {
        internal static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return unit == 0 ? bytes + " B" : string.Format("{0:N1} {1}", size, units[unit]);
        }

        internal static string FormatTime(TimeSpan t)
        {
            if (t.TotalMinutes >= 1) return string.Format("{0}m {1}s", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0:N1}s", t.TotalSeconds);
        }
    }

}
