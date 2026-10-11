using System;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    internal Func<string, string> ArchiveDestinationPicker { get; set; }
    internal string ChooseArchiveDestination(string suggested)
    {
        if (ArchiveDestinationPicker != null) return ArchiveDestinationPicker(suggested);
        using (var picker = new SaveFileDialog {
            Title = "Save full backup as ZIP archive", Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = "zip", AddExtension = true, FileName = suggested,
            CheckPathExists = true, OverwritePrompt = false, RestoreDirectory = true
        })
            return picker.ShowDialog(this) == DialogResult.OK ? picker.FileName : null;
    }
}
