using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GearBackup.Gui
{
    static partial class GuiTheme
    {
        internal sealed class WindowsAppearance
        {
            internal bool Dark, HighContrast;
            internal Color Accent;
        }
        // A provider keeps the palette testable without modifying the user's Windows preferences.
        internal static Func<WindowsAppearance> ReadWindowsAppearance = GetWindowsAppearance;

        [DllImport("dwmapi.dll")]
        static extern int DwmGetColorizationColor(out uint color, out bool opaque);

        static WindowsAppearance GetWindowsAppearance()
        {
            bool darkMode = false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    darkMode = key != null && key.GetValue("AppsUseLightTheme") is int && (int)key.GetValue("AppsUseLightTheme") == 0;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
            Color accent = SystemColors.Highlight;
            uint color; bool opaque;
            if (DwmGetColorizationColor(out color, out opaque) == 0)
                accent = Color.FromArgb((int)((color >> 16) & 255), (int)((color >> 8) & 255), (int)(color & 255));
            return new WindowsAppearance { Dark = darkMode, Accent = accent, HighContrast = SystemInformation.HighContrast };
        }

        static Palette WindowsPalette(WindowsAppearance appearance)
        {
            if (appearance.HighContrast)
                return new Palette {
                    Dark = SystemColors.Window.GetBrightness() < .5f,
                    Ink = SystemColors.WindowText, Muted = SystemColors.WindowText,
                    Accent = SystemColors.Highlight, AccentDark = SystemColors.WindowText, AccentSoft = SystemColors.Window,
                    PrimaryText = SystemColors.HighlightText,
                    Desktop = SystemColors.Control, Background = SystemColors.Control, Surface = SystemColors.Window,
                    Border = SystemColors.WindowText, DisabledSurface = SystemColors.Control, DisabledInk = SystemColors.GrayText
                };
            bool darkMode = appearance.Dark;
            Color accent = appearance.Accent.IsEmpty ? SystemColors.Highlight : appearance.Accent;
            Color surface = darkMode ? Color.FromArgb(32, 32, 32) : Color.White;
            Color ink = darkMode ? Color.FromArgb(242, 242, 242) : Color.FromArgb(26, 26, 26);
            // Preserve the Windows accent on buttons; brighten/darken text accents for readable links.
            Color textAccent = accent;
            for (int i = 0; i < 16 && Contrast(textAccent, surface) < 4.5; i++)
                textAccent = Blend(textAccent, darkMode ? Color.White : Color.Black, .15);
            return new Palette {
                Dark = darkMode, Ink = ink,
                Muted = darkMode ? Color.FromArgb(190, 190, 190) : Color.FromArgb(90, 90, 90),
                Accent = accent, AccentDark = textAccent, AccentSoft = Blend(surface, accent, darkMode ? .18 : .10),
                PrimaryText = Contrast(accent, Color.White) >= Contrast(accent, Color.Black) ? Color.White : Color.Black,
                Desktop = darkMode ? Color.FromArgb(24, 24, 24) : Color.FromArgb(243, 243, 243),
                Background = darkMode ? Color.FromArgb(24, 24, 24) : Color.FromArgb(243, 243, 243), Surface = surface,
                Border = darkMode ? Color.FromArgb(78, 78, 78) : Color.FromArgb(215, 215, 215),
                DisabledSurface = darkMode ? Color.FromArgb(45, 45, 45) : Color.FromArgb(235, 235, 235),
                DisabledInk = darkMode ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120)
            };
        }
        static Color Blend(Color from, Color to, double amount)
        {
            return Color.FromArgb((int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount), (int)Math.Round(from.B + (to.B - from.B) * amount));
        }
        static double Luminance(Color color)
        {
            Func<byte, double> channel = value => { double v = value / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); };
            return .2126 * channel(color.R) + .7152 * channel(color.G) + .0722 * channel(color.B);
        }
        static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }
        internal static void RefreshWindowsTheme()
        {
            if (ThemeName != "windows") return;
            Palette next = WindowsPalette(ReadWindowsAppearance());
            // Native theme changes can generate more notifications. Ignore unchanged preferences.
            if (next.Dark == palette.Dark && next.Ink == Ink && next.Muted == Muted && next.Accent == Accent &&
                next.AccentDark == AccentDark && next.AccentSoft == AccentSoft && next.Desktop == Desktop &&
                next.Background == Background && next.Surface == Surface && next.Border == Border &&
                next.DisabledSurface == DisabledSurface && next.DisabledInk == DisabledInk && next.PrimaryText == palette.PrimaryText) return;
            ApplyTheme("windows");
        }
    }

}
