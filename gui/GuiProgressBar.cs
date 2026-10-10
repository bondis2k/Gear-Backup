using System;
using System.Drawing;
using System.Windows.Forms;

partial class M8Backup
{
    // A flat progress bar in the theme's accent colour (the standard WinForms bar ignores colours under
    // visual styles). Marquee mode is animated by MainForm's display timer calling Advance.
    sealed class GuiProgressBar : Control
    {
        int value, offset;
        ProgressBarStyle style;
        public int Value { get { return value; } set { this.value = value; Invalidate(); } }
        public ProgressBarStyle Style { get { return style; } set { style = value; Invalidate(); } }
        public GuiProgressBar() { DoubleBuffered = true; }
        public void Advance() { if (style == ProgressBarStyle.Marquee) { offset = (offset + 16) % Math.Max(1, Width); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(221, 229, 229));
            using (var brush = new SolidBrush(GuiTheme.Accent))
            {
                if (style == ProgressBarStyle.Marquee) e.Graphics.FillRectangle(brush, offset, 0, Math.Max(30, Width / 5), Height);
                else e.Graphics.FillRectangle(brush, 0, 0, Width * Math.Min(100, Math.Max(0, value)) / 100, Height);
            }
        }
    }

}
