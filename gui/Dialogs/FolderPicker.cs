using System.IO;
using System.Windows.Forms;
using static GearBackup;

static class FolderPicker
{
    internal static void BrowseFolder(IWin32Window owner, TextBox target, bool allowNew, string description = null)
    {
        using (var dialog = new FolderBrowserDialog { Description = description ?? (allowNew ? "Choose a backup or recovery location" : "Choose the card or source folder"), ShowNewFolderButton = allowNew })
        {
            if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
            if (dialog.ShowDialog(owner) == DialogResult.OK) target.Text = dialog.SelectedPath;
        }
    }
}
