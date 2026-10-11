using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Keep folder navigation native and keyboard-accessible, while drawing icons and selection rows
// with the live palette. No shell icon cache or per-theme image assets are needed.
internal sealed class BrowserTree : TreeView
{
    readonly ImageList icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
    readonly List<Bitmap> iconImages = new List<Bitmap>();
    readonly HashSet<TreeNode> expanded = new HashSet<TreeNode>();
    string iconTheme;
    internal BrowserTree()
    {
        BorderStyle = BorderStyle.None; HideSelection = false; ShowLines = false; ShowRootLines = true;
        DrawMode = TreeViewDrawMode.OwnerDrawText; AccessibleName = "Folders and drives";
        ImageList = icons; ImageIndex = SelectedImageIndex = 0;
        AfterExpand += delegate(object sender, TreeViewEventArgs e) { expanded.Add(e.Node); };
        AfterCollapse += delegate(object sender, TreeViewEventArgs e) { expanded.Remove(e.Node); };
        SizeRows();
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (icons != null) SizeRows(); }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); if (icons != null) SizeRows(); }
    internal void RefreshAppearance() { SizeRows(); Invalidate(); }
    void SizeRows()
    {
        int icon = Math.Max(16, Font.Height);
        int height = Math.Max(30, (int)Math.Ceiling(Font.Height * 1.8));
        if (ItemHeight != height) ItemHeight = height;
        string colors = GuiTheme.ThemeName + ":" + GuiTheme.Accent.ToArgb() + ":" + GuiTheme.AccentSoft.ToArgb() + ":" + GuiTheme.Muted.ToArgb();
        if (icons.ImageSize.Width == icon && iconTheme == colors) return;
        // Remember only expanded branches, rather than inspecting every file at each scaling pass.
        var branches = new List<TreeNode>(expanded);
        TreeNode selected = IsHandleCreated ? SelectedNode : null, top = IsHandleCreated ? TopNode : null;
        BeginUpdate();
        try
        {
            icons.Images.Clear();
            foreach (var image in iconImages) image.Dispose();
            iconImages.Clear(); icons.ImageSize = new Size(icon, icon);
            for (int kind = 0; kind < 3; kind++)
            {
                var bitmap = new Bitmap(icon, icon);
                using (var g = Graphics.FromImage(bitmap))
                {
                    if (kind == 0) BrowserIcon.DrawFolder(g, new Rectangle(0, 0, icon, icon));
                    else if (kind == 1) BrowserIcon.DrawFile(g, new Rectangle(0, 0, icon, icon));
                    else BrowserIcon.DrawDrive(g, new Rectangle(0, 0, icon, icon));
                }
                // ImageList retains its source bitmap until its native handle is created.
                iconImages.Add(bitmap); icons.Images.Add(bitmap);
            }
            iconTheme = colors;
            foreach (var node in branches) if (node.TreeView == this) node.Expand();
            if (selected != null && SelectedNode != selected) SelectedNode = selected;
            if (top != null) TopNode = top;
        }
        finally { EndUpdate(); }
    }
    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        if (e.Node.Tag == null) { e.DrawDefault = true; base.OnDrawNode(e); return; }
        bool selected = (e.State & TreeNodeStates.Selected) != 0;
        // OwnerDrawText bounds start AFTER Windows' expander, checkbox and image. Never paint
        // to their left: native checkbox spacing changes independently with Windows DPI.
        int left = Math.Max(0, e.Bounds.Left);
        if (left >= ClientSize.Width) return;
        var row = new Rectangle(left, e.Bounds.Top + 2, Math.Max(1, ClientSize.Width - left - 4), Math.Max(1, e.Bounds.Height - 4));
        var state = e.Graphics.Save();
        e.Graphics.SetClip(new Rectangle(left, e.Bounds.Top, ClientSize.Width - left, e.Bounds.Height), CombineMode.Intersect);
        // Erase Windows' text-sized selection before painting the rounded, full-width row.
        using (var clear = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(clear, new Rectangle(row.Left, e.Bounds.Top, ClientSize.Width - row.Left, e.Bounds.Height));
        using (var fill = new SolidBrush(selected ? GuiTheme.AccentSoft : BackColor))
        using (var shape = GuiTheme.RoundedRectangle(row, GuiTheme.Radius)) e.Graphics.FillPath(fill, shape);
        var text = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, Math.Max(0, ClientSize.Width - e.Bounds.Left - 12), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Node.Text, Font, text, selected ? GuiTheme.AccentDark : GuiTheme.Ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected && Focused)
            using (var stroke = new Pen(GuiTheme.Accent))
            using (var shape = GuiTheme.RoundedRectangle(row, GuiTheme.Radius)) e.Graphics.DrawPath(stroke, shape);
        e.Graphics.Restore(state);
        base.OnDrawNode(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { icons.Dispose(); foreach (var image in iconImages) image.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class BrowserIcon : Control
{
    internal BrowserIcon() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(GuiTheme.AccentSoft))
        using (var shape = GuiTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 12)) e.Graphics.FillPath(fill, shape);
        DrawFolder(e.Graphics, new Rectangle(Width / 4, Height / 4, Width / 2, Height / 2));
        base.OnPaint(e);
    }
    internal static void DrawFolder(Graphics g, Rectangle box)
    {
        var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = box.Left + box.Width * .05f, y = box.Top + box.Height * .2f, w = box.Width * .9f, h = box.Height * .65f;
        using (var shape = new GraphicsPath())
        using (var fill = new SolidBrush(GuiTheme.AccentSoft))
        using (var line = new Pen(GuiTheme.Accent, Math.Max(1, box.Width / 16f)))
        {
            shape.AddLines(new[] { new PointF(x, y), new PointF(x + w * .4f, y), new PointF(x + w * .52f, y + h * .2f),
                new PointF(x + w, y + h * .2f), new PointF(x + w, y + h), new PointF(x, y + h) });
            shape.CloseFigure(); g.FillPath(fill, shape); g.DrawPath(line, shape);
        }
        g.Restore(state);
    }
    internal static void DrawDrive(Graphics g, Rectangle box)
    {
        using (var line = new Pen(GuiTheme.Muted, Math.Max(1, box.Width / 16f)))
        {
            g.DrawRectangle(line, box.Left + box.Width / 8, box.Top + box.Height / 4, box.Width * 3 / 4, box.Height / 2);
            g.DrawLine(line, box.Left + box.Width / 4, box.Top + box.Height * 5 / 8, box.Left + box.Width / 2, box.Top + box.Height * 5 / 8);
        }
    }
    internal static void DrawFile(Graphics g, Rectangle box)
    {
        var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = box.Left + box.Width * .2f, y = box.Top + box.Height * .1f, w = box.Width * .65f, h = box.Height * .8f, fold = w * .35f;
        using (var shape = new GraphicsPath())
        using (var line = new Pen(GuiTheme.Muted, Math.Max(1, box.Width / 16f)))
        {
            shape.AddLines(new[] { new PointF(x, y), new PointF(x + w - fold, y), new PointF(x + w, y + fold),
                new PointF(x + w, y + h), new PointF(x, y + h) });
            shape.CloseFigure(); g.DrawPath(line, shape);
            g.DrawLines(line, new[] { new PointF(x + w - fold, y), new PointF(x + w - fold, y + fold), new PointF(x + w, y + fold) });
        }
        g.Restore(state);
    }
}
