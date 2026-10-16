using System;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    internal partial class MainForm
    {
        bool windowsThemeRefreshPending;
        protected override void WndProc(ref Message message)
        {
            int kind = message.Msg;
            base.WndProc(ref message);
            // Windows broadcasts preference, theme, system-colour and DWM accent changes.
            if (kind != 0x001A && kind != 0x0015 && kind != 0x031A && kind != 0x0320) return;
            if (GuiTheme.ThemeName != "windows" || windowsThemeRefreshPending || !IsHandleCreated || IsDisposed) return;
            windowsThemeRefreshPending = true;
            BeginInvoke((Action)delegate {
                windowsThemeRefreshPending = false;
                if (!IsDisposed) GuiTheme.RefreshWindowsTheme();
            });
        }
    }

}
