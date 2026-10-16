using System;
using System.Drawing;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    internal sealed class SearchDialog : DialogForm
    {
        public readonly TextBox Input = new TextBox();
        public SearchDialog(string heading, string hint) : base(heading + Environment.NewLine + hint)
        {
            ClientSize = new Size(650, 230);
            layout.Controls.Add(new FieldPanel(Input) { Dock = DockStyle.Top, Margin = Padding.Empty }, 0, 1);
        }
    }

}
