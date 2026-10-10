using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

partial class M8Backup
{
    // Base for the GUI's dialogs. The engines' console questions become the heading: "[R]eplace" turns
    // into "Replace", and console hints like "(y/n)" or "Enter = cancel" are removed.
    abstract class DialogForm : Form
    {
        public readonly Button Accept = GuiTheme.Button("Continue", true);
        protected readonly TableLayoutPanel layout;
        protected readonly FlowLayoutPanel buttons;
        protected DialogForm(string heading)
        {
            Text = "M8 Backup"; Font = new Font("Segoe UI", 10); BackColor = GuiTheme.Background;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(700, 430); MinimumSize = new Size(560, 300); AutoScaleMode = AutoScaleMode.Dpi;
            if (GuiHeadless) Opacity = 0;
            layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            heading = Regex.Replace(heading, @"\[([A-Za-z])\]", "$1");
            heading = Regex.Replace(heading, @"\s*\(Enter.*?\)|\s*Enter.*$|\s*\(y/n\)", "", RegexOptions.IgnoreCase);
            var title = GuiTheme.Label(heading.Trim().TrimEnd(',', ':'), 12, GuiTheme.Ink); title.AutoSize = false; title.Dock = DockStyle.Fill;
            layout.Controls.Add(title, 0, 0);
            buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            var dismiss = GuiTheme.Button("Cancel", false); dismiss.DialogResult = DialogResult.Cancel; CancelButton = dismiss;
            buttons.Controls.Add(Accept); buttons.Controls.Add(dismiss); layout.Controls.Add(buttons, 0, 2);
            Accept.DialogResult = DialogResult.OK; AcceptButton = Accept; Controls.Add(layout);
        }
    }
}
