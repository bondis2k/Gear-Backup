using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// One button per option of a question, labelled with the option's label; the first is the main one.
internal sealed class ChoiceDialog : DialogForm
{
    public char Key;
    public ChoiceDialog(string question, IList<Option> options) : base(question)
    {
        ClientSize = new Size(720, 260); buttons.Controls.Remove(Accept);
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        for (int i = 0; i < options.Count; i++)
        {
            Option option = options[i];
            var button = GuiTheme.Button(option.Label, i == 0); button.Margin = new Padding(0, 0, 10, 10);
            button.Click += delegate { Key = option.Key; DialogResult = DialogResult.OK; Close(); };
            choices.Controls.Add(button);
        }
        AcceptButton = null; layout.Controls.Add(choices, 0, 1);
    }
}
