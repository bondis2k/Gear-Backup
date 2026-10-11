using System.IO;
using System.Windows.Forms;

static class FolderPicker
{
    internal static void BrowseFolder(IWin32Window owner, TextBox target, bool allowNew, string description = null)
    {
        string heading = description ?? (allowNew ? "Choose a backup or recovery location" : "Choose the card or source folder");
        using (var dialog = new FolderPickerDialog(heading, Directory.Exists(target.Text) ? target.Text : null, allowNew))
            if (dialog.ShowDialog(owner) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }
}
