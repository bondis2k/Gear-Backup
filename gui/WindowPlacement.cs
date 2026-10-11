using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using static Workflow;

// Remembers where the main window was, so the app opens there next time. The position is kept in
// GearBackup.Window.ini beside GearBackup.ini (in the installed settings folder, or beside a portable
// copy), never in the registry. A position that's no longer on any screen (a disconnected monitor) is
// ignored and the window opens centred as before.
static class WindowPlacement
{
    internal const string FileName = "GearBackup.Window.ini";

    // Applies the saved position and size; false when there is none to use.
    internal static bool Restore(Form form, string settingsDirectory)
    {
        try
        {
            string path = Path.Combine(settingsDirectory, FileName);
            if (!File.Exists(path)) return false;
            bool maximized;
            Rectangle? bounds = Parse(Records.ReadLines(path, Records.SmallLimit, "Window position"), out maximized);
            var screens = new List<Rectangle>();
            foreach (Screen screen in Screen.AllScreens) screens.Add(screen.WorkingArea);
            if (bounds == null || !OnScreen(bounds.Value, screens)) return false;
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds.Value;
            if (maximized) form.WindowState = FormWindowState.Maximized;
            return true;
        }
        catch (Exception) { return false; }  // A damaged file only loses the remembered position.
    }

    // Saves the window's normal position and size (the ones it returns to when not maximized).
    internal static void Save(Form form, string settingsDirectory)
    {
        try
        {
            Rectangle bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            AtomicText(Path.Combine(settingsDirectory, FileName), Format(bounds, form.WindowState == FormWindowState.Maximized));
        }
        catch (Exception) { }  // Not being able to save the position must never get in the way of closing.
    }

    internal static string Format(Rectangle bounds, bool maximized)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "; Gear Backup main window position, saved when it closes.\r\nLeft={0}\r\nTop={1}\r\nWidth={2}\r\nHeight={3}\r\nMaximized={4}\r\n",
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized ? "yes" : "no");
    }

    // The saved bounds, or null when the file doesn't hold a usable position.
    internal static Rectangle? Parse(IEnumerable<string> lines, out bool maximized)
    {
        maximized = false;
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            int equals = line.IndexOf('=');
            if (line.TrimStart().StartsWith(";") || equals <= 0) continue;
            string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
            int number;
            if (key.Equals("Maximized", StringComparison.OrdinalIgnoreCase)) maximized = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) values[key] = number;
        }
        int left, top, width, height;
        if (!values.TryGetValue("Left", out left) || !values.TryGetValue("Top", out top) ||
            !values.TryGetValue("Width", out width) || !values.TryGetValue("Height", out height) ||
            width < 200 || height < 150 || width > 20000 || height > 20000)
            return null;
        return new Rectangle(left, top, width, height);
    }

    // Whether the window's title bar would be on one of the screens, so it can be seen and dragged.
    internal static bool OnScreen(Rectangle bounds, IList<Rectangle> screens)
    {
        var title = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 10);
        foreach (Rectangle area in screens) if (area.Contains(title)) return true;
        return false;
    }
}
