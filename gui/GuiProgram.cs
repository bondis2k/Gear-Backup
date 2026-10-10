using System;
using System.Drawing;
using System.Windows.Forms;

class M8Gui
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && (args[0] == "--render-preview" || args[0] == "--render-settings"))
        {
            M8Backup.GuiHeadless = true;
            using (var form = args[0] == "--render-settings" ? M8Backup.CreateSettingsPreview() : M8Backup.CreateGui(true))
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
        Application.Run(M8Backup.CreateGui(false));
        return 0;
    }
}

partial class M8Backup
{
    internal static Form CreateGui(bool designPreview) { return new MainForm(designPreview); }
    internal static Form CreateSettingsPreview() { return new SettingsDialog(new Config()); }
    internal static bool GuiHeadless;

}
