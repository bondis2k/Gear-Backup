using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

partial class GearBackup
{
    static partial class GuiTheme
    {
        const int ImmersiveDarkMode = 20;
        const int CaptionColor = 35, CaptionTextColor = 36, WindowBorderColor = 34;
        static readonly Lazy<Icon> windowIcon = new Lazy<Icon>(delegate {
            using (var stream = typeof(GearBackup).Assembly.GetManifestResourceStream("Backup.ico"))
            using (var icon = new Icon(stream)) return (Icon)icon.Clone();
        });

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static void StyleWindow(Form form)
        {
            form.Icon = windowIcon.Value;
            // Reapply after Windows recreates a handle, without forcing early handle creation.
            form.HandleCreated -= WindowHandleCreated;
            form.HandleCreated += WindowHandleCreated;
            if (form.IsHandleCreated) ApplyTitleBar(form);
        }

        static void WindowHandleCreated(object sender, EventArgs e) { ApplyTitleBar((Form)sender); }

        static void ApplyTitleBar(Form form)
        {
            int dark = ThemeName == "original-dark" ? 1 : 0;
            // Unsupported Windows versions return an HRESULT and retain their normal title bar.
            DwmSetWindowAttribute(form.Handle, ImmersiveDarkMode, ref dark, sizeof(int));
            // Windows 11 supports explicit caption colours, avoiding its near-black dark default.
            int caption = ColorTranslator.ToWin32(ThemeName == "retro" ? Accent : Background);
            int text = ColorTranslator.ToWin32(ThemeName == "retro" ? Color.White : Ink);
            int border = ColorTranslator.ToWin32(Border);
            DwmSetWindowAttribute(form.Handle, CaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(form.Handle, CaptionTextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(form.Handle, WindowBorderColor, ref border, sizeof(int));
        }
    }
}
