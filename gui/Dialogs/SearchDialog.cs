using System;
using System.Drawing;
using System.Windows.Forms;

partial class GearBackup
{
    sealed class SearchDialog : DialogForm
    {
        public readonly TextBox Input = new TextBox();
        public SearchDialog(string heading, string hint) : base(heading + Environment.NewLine + hint)
        {
            ClientSize = new Size(650, 230); Input.Dock = DockStyle.Top;
            layout.Controls.Add(Input, 0, 1);
        }
    }
}
