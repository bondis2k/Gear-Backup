using System.IO;
using System.Windows.Forms;

partial class M8Backup
{
    static void BrowseFolder(IWin32Window owner, TextBox target, bool allowNew)
    {
        using (var dialog = new FolderBrowserDialog { Description = allowNew ? "Choose a backup or recovery location" : "Choose the M8 card or source folder", ShowNewFolderButton = allowNew })
        {
            if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
            if (dialog.ShowDialog(owner) == DialogResult.OK) target.Text = dialog.SelectedPath;
        }
    }

}
