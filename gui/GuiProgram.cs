using System;
using System.Drawing;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // Entry point of GearBackup.GUI.exe.
    static class GearGui
    {
        internal static Form CreateGui(bool designPreview) { return new MainForm(designPreview); }
        internal static Form CreateSettingsPreview() { return new SettingsDialog(new Config()); }
        // True while rendering previews: windows and dialogs stay invisible.
        internal static bool GuiHeadless;

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Design aid: renders the main window or the settings dialog to a PNG without showing it.
            if (args.Length == 2 && (args[0] == "--render-preview" || args[0] == "--render-settings"))
            {
                GuiHeadless = true;
                using (var form = args[0] == "--render-settings" ? CreateSettingsPreview() : CreateGui(true))
                {
                    form.Opacity = 0;
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            Application.Run(CreateGui(false));
            return 0;
        }
    }

}
