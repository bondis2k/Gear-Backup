using System;
using System.Drawing;
using System.Windows.Forms;

// A flat progress bar in the theme's accent colour (the standard WinForms bar ignores colours under
// visual styles). Marquee mode is animated by MainForm's display timer calling Advance.
internal sealed class GuiProgressBar : Control
{
    int value, offset;
    ProgressBarStyle style;
    public int Value { get { return value; } set { this.value = value; Invalidate(); } }
    public ProgressBarStyle Style { get { return style; } set { style = value; Invalidate(); } }
    public GuiProgressBar() { DoubleBuffered = true; AccessibleRole = AccessibleRole.ProgressBar; AccessibleName = "Operation progress"; }
    public void Advance() { if (style == ProgressBarStyle.Marquee) { offset = (offset + 16) % Math.Max(1, Width); Invalidate(); } }
    // A rounded track with a rounded bar: the done part, or a moving segment while the total is unknown.
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(GuiTheme.ContainerBackground(Parent));
        float radius = Height / 2f;
        using (var track = GuiTheme.RoundedRectangle(new RectangleF(0, 0, Width - 1, Height - 1), radius))
        using (var brush = new SolidBrush(GuiTheme.Border))
            g.FillPath(brush, track);
        float left = 0, width;
        if (style == ProgressBarStyle.Marquee) { left = offset; width = Math.Max(40, Width / 5); }
        else width = (Width - 1) * Math.Min(100, Math.Max(0, value)) / 100f;
        width = Math.Min(width, Width - 1 - left);
        if (width < 1) return;
        using (var bar = GuiTheme.RoundedRectangle(new RectangleF(left, 0, width, Height - 1), radius))
        using (var brush = new SolidBrush(GuiTheme.Accent))
            g.FillPath(brush, bar);
    }
}
