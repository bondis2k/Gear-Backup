using System.Drawing;
using System.Windows.Forms;

partial class M8Backup
{
    // One button per allowed key of a console-style question. The same key can mean different things in
    // different questions (r = Resume or Replace), so labels depend on the full set of keys.
    sealed class ChoiceDialog : DialogForm
    {
        public char Key;
        public ChoiceDialog(string question, string allowed) : base(question)
        {
            ClientSize = new Size(720, 260); buttons.Controls.Remove(Accept);
            var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            foreach (char key in allowed)
            {
                char captured = key;
                string label = key == 'y' ? "Yes" : key == 'n' ? (allowed == "rnc" ? "New full backup" : "No") :
                    key == 'r' ? (allowed == "rnc" ? "Resume" : "Replace") : key == 'k' ? "Keep existing" : key == 'c' ? "Cancel" :
                    key == 'a' ? "All files" : key == 'b' ? "Bundle" : key == 's' ? "Single file" :
                    key == 'd' ? (allowed == "od" ? "Dated name" : "Song + samples") : key == 'o' ? "Original name" :
                    key == 'm' ? "Merge" : key == 'e' ? "Exact snapshot" : key.ToString().ToUpperInvariant();
                var button = GuiTheme.Button(label, key == allowed[0]); button.Margin = new Padding(0, 0, 10, 10);
                button.Click += delegate { Key = captured; DialogResult = DialogResult.OK; Close(); };
                choices.Controls.Add(button);
            }
            AcceptButton = null; layout.Controls.Add(choices, 0, 1);
        }
    }

}
