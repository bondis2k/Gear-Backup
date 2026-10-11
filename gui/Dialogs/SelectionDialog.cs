using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal class SelectionDialog : DialogForm
{
    public readonly ListBox List = new ListBox();
    public SelectionDialog(string heading, List<string> choices) : base(heading)
    {
        List.Dock = DockStyle.Fill; List.BorderStyle = BorderStyle.None;
        List.Font = GuiTheme.Font(10.5f); List.HorizontalScrollbar = true; List.IntegralHeight = false;
        // Owner-drawn so the selected row uses the app's selection colours instead of Windows' blue.
        List.DrawMode = DrawMode.OwnerDrawFixed; List.ItemHeight = List.Font.Height + 6;
        List.DrawItem += delegate(object sender, DrawItemEventArgs e) {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? GuiTheme.AccentSoft : List.BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index < 0) return;
            TextRenderer.DrawText(e.Graphics, List.GetItemText(List.Items[e.Index]), List.Font, new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                GuiTheme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        List.Items.AddRange(choices.ToArray()); if (choices.Count > 0) List.SelectedIndex = 0;
        Accept.Enabled = choices.Count > 0; var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty };
        card.Controls.Add(List); layout.Controls.Add(card, 0, 1);
        List.DoubleClick += delegate { if (List.SelectedIndex >= 0) { DialogResult = DialogResult.OK; Close(); } };
    }
}
