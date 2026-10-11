using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static partial class GuiTheme
{
    const int ImmersiveDarkMode = 20;
    const int CaptionColor = 35, CaptionTextColor = 36, WindowBorderColor = 34;
    static readonly Lazy<Icon> windowIcon = new Lazy<Icon>(delegate {
        using (var stream = typeof(GuiTheme).Assembly.GetManifestResourceStream("Backup.ico"))
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

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window, string appName, string idList);

    // Windows paints native scroll bars and drop-down buttons light unless told to use its dark control
    // themes; the other themes use the default look. Unsupported Windows versions ignore the request.
    static void ApplyNativeTheme(Control control)
    {
        string name;
        if (control is ComboBox) name = "DarkMode_CFD";
        else if (control is ListBox || control is TextBoxBase || control is ScrollBar || control is DataGridView || control is ListView ||
                 control is TreeView || (control is ScrollableControl && ((ScrollableControl)control).AutoScroll)) name = "DarkMode_Explorer";
        else return;
        control.HandleCreated -= NativeControlCreated;
        control.HandleCreated += NativeControlCreated;
        if (control.IsHandleCreated) SetWindowTheme(control.Handle, IsDark ? name : null, null);
    }

    // Windows draws a disabled check box or radio button's text in its own dark grey, which vanishes on
    // the dark theme's background, so that text is repainted in the theme's disabled colour.
    static void StyleDisabledText(ButtonBase button)
    {
        button.Paint -= PaintDisabledText;
        button.Paint += PaintDisabledText;
    }

    static void PaintDisabledText(object sender, PaintEventArgs e)
    {
        var button = (ButtonBase)sender;
        if (button.Enabled || !IsDark || string.IsNullOrEmpty(button.Text)) return;
        float scale = e.Graphics.DpiX / 96f;
        // The text starts a fixed distance after the check box, 16 pixels at 96 DPI.
        int left = (int)Math.Round(16 * scale);
        Color back = Color.Empty;
        for (Control parent = button; parent != null && back.IsEmpty; parent = parent.Parent)
            if (parent.BackColor.A == 255) back = parent.BackColor;
        int cover = left - (int)Math.Round(scale);
        var area = new Rectangle(cover, 0, Math.Max(0, button.Width - cover), button.Height);
        using (var brush = new SolidBrush(back.IsEmpty ? Background : back)) e.Graphics.FillRectangle(brush, area);
        var text = new Rectangle(left, 0, Math.Max(0, button.Width - left), button.Height);
        TextRenderer.DrawText(e.Graphics, button.Text, button.Font, text, DisabledInk,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.HidePrefix);
    }

    static void NativeControlCreated(object sender, EventArgs e) { ApplyNativeTheme((Control)sender); }

    static void WindowHandleCreated(object sender, EventArgs e) { ApplyTitleBar((Form)sender); }

    static void ApplyTitleBar(Form form)
    {
        int dark = IsDark ? 1 : 0;
        // Unsupported Windows versions return an HRESULT and retain their normal title bar.
        DwmSetWindowAttribute(form.Handle, ImmersiveDarkMode, ref dark, sizeof(int));
        // Windows 11 supports explicit caption colours, avoiding its near-black dark default.
        // Let Windows choose its own caption/accent and inactive-window colours in system mode.
        int caption = ThemeName == "windows" ? -1 : ColorTranslator.ToWin32(ThemeName == "retro" ? Accent : Background);
        int text = ThemeName == "windows" ? -1 : ColorTranslator.ToWin32(ThemeName == "retro" ? Color.White : Ink);
        int border = ThemeName == "windows" ? -1 : ColorTranslator.ToWin32(Border);
        DwmSetWindowAttribute(form.Handle, CaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(form.Handle, CaptionTextColor, ref text, sizeof(int));
        DwmSetWindowAttribute(form.Handle, WindowBorderColor, ref border, sizeof(int));
    }
}
