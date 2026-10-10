using System.Drawing;
using System.Windows.Forms;

partial class M8Backup
{
    // Colours and control factories for the window's flat look.
    static class GuiTheme
    {
        public static readonly Color Ink = Color.FromArgb(29, 45, 54);
        public static readonly Color Muted = Color.FromArgb(99, 116, 123);
        public static readonly Color Accent = Color.FromArgb(0, 117, 106);
        public static readonly Color Background = Color.FromArgb(243, 246, 246);

        public static Label Label(string text, float size, Color color)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = color,
                Font = new Font("Segoe UI", size), Margin = new Padding(0, 0, 0, 8) };
        }
        public static Button Button(string text, bool primary)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 36),
                Padding = new Padding(14, 3, 14, 3), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
                Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0) };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(214, 224, 224);
            return button;
        }

    }

}
