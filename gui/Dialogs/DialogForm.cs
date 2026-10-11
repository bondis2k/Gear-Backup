using System.Drawing;
using System.Windows.Forms;
using static GearGui;

// Base for the GUI's dialogs: a heading (the question), a content area, and Continue/Cancel buttons.
internal abstract class DialogForm : Form
{
    public readonly Button Accept = GuiTheme.Button("Continue", true);
    protected readonly TableLayoutPanel layout;
    protected readonly FlowLayoutPanel buttons;
    protected DialogForm(string heading)
    {
        Text = AppIdentity.Name; Font = new Font("Segoe UI", 10); BackColor = GuiTheme.Background;
        GuiTheme.StyleWindow(this);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;
        ClientSize = new Size(700, 430); MinimumSize = new Size(560, 300); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        if (GuiHeadless) Opacity = 0;
        layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = GuiTheme.Label(heading.Trim().TrimEnd(',', ':'), 12, GuiTheme.Ink); title.AutoSize = false; title.Dock = DockStyle.Fill;
        title.Font = GuiTheme.Heading(12.5f);
        layout.Controls.Add(title, 0, 0);
        buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var dismiss = GuiTheme.Button("Cancel", false); dismiss.DialogResult = DialogResult.Cancel; CancelButton = dismiss;
        Accept.Margin = Padding.Empty; buttons.Controls.Add(Accept); buttons.Controls.Add(dismiss); layout.Controls.Add(buttons, 0, 2);
        Accept.DialogResult = DialogResult.OK; AcceptButton = Accept; Controls.Add(layout);
        Shown += delegate { GuiTheme.FitToScreen(this); };
    }
}
