using System.Drawing;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    internal sealed class SampleRestoreDialog : DialogForm
    {
        internal SampleRestoreDialog(string question) : base("Restore sample from backup?")
        {
            Text = "Restore sample · " + AppIdentity.Name;
            ClientSize = new Size(760, 440);
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = GuiTheme.Surface, Padding = new Padding(12) };
            var details = new Label { Name = "SampleRestoreDetails", AccessibleName = "Sample restore details",
                Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false,
                BackColor = GuiTheme.Surface, ForeColor = GuiTheme.Ink, Text = question };
            body.SizeChanged += delegate {
                details.MaximumSize = new Size(System.Math.Max(50, body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth), 0);
            };
            body.Controls.Add(details);
            layout.Controls.Add(body, 0, 1);
            Accept.Text = "Open Restore";
            Accept.Name = "ConfirmSampleRestore";
            ((Control)CancelButton).Name = "CancelSampleRestore";
            // Enter defaults to cancellation; restoring remains an explicit choice.
            AcceptButton = CancelButton;
            Shown += delegate { ((Control)CancelButton).Select(); };
            GuiTheme.ApplyTo(this);
        }
    }
}
