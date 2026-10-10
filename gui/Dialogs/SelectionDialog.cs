using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

partial class M8Backup
{
    class SelectionDialog : DialogForm
    {
        public readonly ListBox List = new ListBox();
        public SelectionDialog(string heading, List<string> choices) : base(heading)
        {
            List.Dock = DockStyle.Fill; List.BorderStyle = BorderStyle.FixedSingle;
            List.Font = new Font("Consolas", 10); List.HorizontalScrollbar = true;
            List.Items.AddRange(choices.ToArray()); if (choices.Count > 0) List.SelectedIndex = 0;
            Accept.Enabled = choices.Count > 0; layout.Controls.Add(List, 0, 1);
            List.DoubleClick += delegate { if (List.SelectedIndex >= 0) { DialogResult = DialogResult.OK; Close(); } };
        }
    }
}
