using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using static GearBackup;

internal class SelectionDialog : DialogForm
{
    public readonly ListBox List = new ListBox();
    public SelectionDialog(string heading, List<string> choices) : base(heading)
    {
        List.Dock = DockStyle.Fill; List.BorderStyle = BorderStyle.None;
        List.Font = GuiTheme.Font(10.5f); List.HorizontalScrollbar = true; List.IntegralHeight = false;
        List.Items.AddRange(choices.ToArray()); if (choices.Count > 0) List.SelectedIndex = 0;
        Accept.Enabled = choices.Count > 0; var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty };
        card.Controls.Add(List); layout.Controls.Add(card, 0, 1);
        List.DoubleClick += delegate { if (List.SelectedIndex >= 0) { DialogResult = DialogResult.OK; Close(); } };
    }
}
