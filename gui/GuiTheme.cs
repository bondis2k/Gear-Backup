using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static GearGui;

// The app's look: colours, fonts, and the controls that draw themselves in that style (rounded buttons
// and cards, flat tabs, light tables). Windows and dialogs build their controls through these.
static partial class GuiTheme
{
    public static Color Danger { get { return ThemeName == "original-dark" ? Color.FromArgb(255, 156, 139) : Color.FromArgb(175, 69, 47); } }
    public const int Radius = 8;

    public static Font Font(float size, FontStyle style = FontStyle.Regular) { return new Font("Segoe UI", size, style); }
    public static Font Heading(float size) { return new Font("Segoe UI Semibold", size); }

    public static Color ContainerBackground(Control control)
    {
        for (Control parent = control; parent != null; parent = parent.Parent)
            if (parent.BackColor.A == 255) return parent.BackColor;
        return Background;
    }

    public static Label Label(string text, float size, Color color)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = color, Font = Font(size), Margin = new Padding(0, 0, 0, 8) };
    }

    // A section heading inside a card, e.g. "Activity".
    public static Label SectionTitle(string text)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = Ink, Font = Heading(11), Margin = new Padding(0, 4, 0, 8) };
    }

    // primary: the filled main action; otherwise a white button with a border.
    public static Button Button(string text, bool primary)
    {
        return new ThemedButton(text, primary ? ButtonKind.Primary : ButtonKind.Secondary);
    }

    // A borderless button for toolbars and the header (Help, Settings, Dashboard, ...).
    public static Button GhostButton(string text)
    {
        return new ThemedButton(text, ButtonKind.Ghost);
    }

    // Tables: a light header, horizontal lines only, roomy rows and a tinted selection.
    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.GridColor = Border;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle {
            BackColor = Background, ForeColor = Ink, Font = Heading(9),
            SelectionBackColor = Background, SelectionForeColor = Ink,
            Padding = new Padding(6, 8, 6, 8), WrapMode = DataGridViewTriState.True };
        grid.DefaultCellStyle = new DataGridViewCellStyle {
            BackColor = Surface, ForeColor = Ink, Font = Font(9.5f), Padding = new Padding(6, 6, 6, 6),
            SelectionBackColor = AccentSoft, SelectionForeColor = Ink };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface };
        grid.RowTemplate.Height = 36;
    }

    // Flat tabs: the selected one is underlined in the accent colour.
    // Flat tabs: Windows' own tab strip is hidden under a strip drawn in the app's style (the selected
    // tab underlined in the accent colour). The TabControl still holds the pages and the selection, so
    // code and keyboard use stay the same. Returns the control to add to the window.
    public static Control Tabbed(TabControl tabs)
    {
        tabs.Appearance = TabAppearance.FlatButtons;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(0, 1);
        tabs.Dock = DockStyle.None;
        foreach (TabPage page in tabs.TabPages) page.BackColor = Surface;
        // The tab control is placed so that only its page area shows: its own frame and the leftover
        // strip fall outside the clipping panel.
        var clip = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
        clip.Controls.Add(tabs);
        bool aligning = false;
        Action align = delegate {
            if (aligning) return;
            aligning = true;
            try {
                Rectangle page = tabs.DisplayRectangle;
                int left = page.Left, top = page.Top, right = tabs.Width - page.Right, bottom = tabs.Height - page.Bottom;
                var bounds = new Rectangle(-left, -top, clip.Width + left + right, clip.Height + top + bottom);
                if (tabs.Bounds != bounds) tabs.Bounds = bounds;
            }
            finally { aligning = false; }
        };
        clip.Resize += delegate { align(); };
        clip.Layout += delegate { align(); };
        // DPI scaling can resize the child after its clipping panel; keep the native frame clipped.
        tabs.SizeChanged += delegate { align(); };
        var container = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
        container.Controls.Add(clip);
        container.Controls.Add(new TabStrip(tabs) { Dock = DockStyle.Top });
        return container;
    }

    // Lists keep the standard Windows look (a flat list loses its border), in the app's font.
    public static void StyleComboBox(ComboBox box)
    {
        box.Font = Font(10);
        box.ForeColor = Ink;
        // Windows visual styles otherwise paint the selected field white in dark mode.
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = box.Font.Height + 4;
        box.DrawItem += delegate(object sender, DrawItemEventArgs e) {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color background = ThemeName == "original-dark" ? (selected ? AccentSoft : Surface) :
                selected ? SystemColors.Highlight : SystemColors.Window;
            Color text = ThemeName != "original-dark" && selected ? SystemColors.HighlightText : Ink;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            string value = e.Index >= 0 && e.Index < box.Items.Count ? box.GetItemText(box.Items[e.Index]) : box.Text;
            var bounds = new Rectangle(e.Bounds.X + 3, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 6), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, value, box.Font, bounds, box.Enabled ? text : DisabledInk,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Fits a window that has just been shown onto its screen: a window shown over another one (a tool
    // window or dialog) is centred on it, the main window keeps a restored position, and otherwise it is
    // centred on the screen. It is always kept fully within the screen's working area.
    public static void FitToScreen(Form form)
    {
        ApplyTo(form);
        if (GuiHeadless || form.WindowState != FormWindowState.Normal) return;
        Form owner = form.Owner;
        Rectangle? over = owner != null && owner.Visible && owner.WindowState != FormWindowState.Minimized ? owner.Bounds : (Rectangle?)null;
        Rectangle area = over != null ? Screen.FromRectangle(over.Value).WorkingArea : Screen.FromControl(form).WorkingArea;
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
        form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
        form.Location = PlaceWindow(form.Size, over, form.StartPosition == FormStartPosition.Manual ? form.Location : (Point?)null, area);
    }

    // Where a window of this size goes: centred over `over` when given, else at `kept`, else centred in
    // the area; then moved as needed to lie fully inside the area.
    internal static Point PlaceWindow(Size size, Rectangle? over, Point? kept, Rectangle area)
    {
        Point p = over != null
            ? new Point(over.Value.Left + (over.Value.Width - size.Width) / 2, over.Value.Top + (over.Value.Height - size.Height) / 2)
            : kept ?? new Point(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2);
        return new Point(Math.Max(area.Left, Math.Min(p.X, area.Right - size.Width)), Math.Max(area.Top, Math.Min(p.Y, area.Bottom - size.Height)));
    }
}

internal enum ButtonKind { Primary, Secondary, Ghost }

// The tab headers of a TabControl (see GuiTheme.Tabbed): one per page, the selected one underlined.
internal sealed class TabStrip : FlowLayoutPanel
{
    readonly TabControl tabs;

    public TabStrip(TabControl tabControl)
    {
        tabs = tabControl;
        Height = 46;
        BackColor = GuiTheme.Surface;
        WrapContents = false;
        Padding = new Padding(4, 4, 4, 0);
        Margin = Padding.Empty;
        for (int i = 0; i < tabs.TabPages.Count; i++) Controls.Add(new TabStripItem(tabs, i));
        tabs.SelectedIndexChanged += delegate { foreach (Control item in Controls) item.Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var line = new SolidBrush(GuiTheme.Border)) e.Graphics.FillRectangle(line, 0, Height - 1, Width, 1);
    }
}

internal sealed class TabStripItem : Control
{
    readonly TabControl tabs;
    readonly int index;
    bool hover;

    public TabStripItem(TabControl tabControl, int page)
    {
        tabs = tabControl;
        index = page;
        Text = tabs.TabPages[page].Text;
        AccessibleRole = AccessibleRole.PageTab;
        AccessibleName = Text;
        Font = GuiTheme.Heading(10);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 4, 0);
        Size = new Size(TextRenderer.MeasureText(Text, Font).Width + 36, 41);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MouseEnter += delegate { hover = true; Invalidate(); };
        MouseLeave += delegate { hover = false; Invalidate(); };
        Click += delegate { tabs.SelectedIndex = index; tabs.Focus(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool selected = tabs.SelectedIndex == index;
        e.Graphics.Clear(hover && !selected ? GuiTheme.AccentSoft : GuiTheme.Surface);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Height - 4), selected ? GuiTheme.Ink : GuiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (selected)
            using (var line = new SolidBrush(GuiTheme.Accent)) e.Graphics.FillRectangle(line, 8, Height - 3, Width - 16, 3);
    }
}

// A button that draws itself: rounded, with hover and pressed states and a focus ring. It is still a
// normal Button, so clicks, keyboard use, DialogResult and accessibility work as usual.
internal sealed class ThemedButton : Button
{
    readonly ButtonKind kind;
    bool hover, pressed;

    public ThemedButton(string text, ButtonKind buttonKind)
    {
        kind = buttonKind;
        Text = text;
        AccessibleName = text;
        AutoSize = true;
        MinimumSize = new Size(buttonKind == ButtonKind.Ghost ? 0 : 96, 36);
        Padding = new Padding(buttonKind == ButtonKind.Ghost ? 8 : 14, 3, buttonKind == ButtonKind.Ghost ? 8 : 14, 3);
        Margin = new Padding(0, 0, 8, 0);
        Font = GuiTheme.Font(10);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MouseEnter += delegate { hover = true; Invalidate(); };
        MouseLeave += delegate { hover = false; pressed = false; Invalidate(); };
        MouseDown += delegate { pressed = true; Invalidate(); };
        MouseUp += delegate { pressed = false; Invalidate(); };
        EnabledChanged += delegate { Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color outside = GuiTheme.ContainerBackground(Parent);
        using (var clear = new SolidBrush(outside)) g.FillRectangle(clear, ClientRectangle);

        Color fill, text, border;
        if (!Enabled)
        {
            fill = kind == ButtonKind.Ghost ? outside : GuiTheme.DisabledSurface;
            text = GuiTheme.DisabledInk;
            border = kind == ButtonKind.Secondary ? GuiTheme.Border : fill;
        }
        else if (kind == ButtonKind.Primary)
        {
            fill = pressed || hover ? GuiTheme.AccentDark : GuiTheme.Accent;
            text = GuiTheme.PrimaryInk;
            border = fill;
        }
        else
        {
            fill = pressed ? GuiTheme.Background : hover ? GuiTheme.AccentSoft : kind == ButtonKind.Ghost ? outside : GuiTheme.ButtonSurface;
            text = GuiTheme.Ink;
            border = kind == ButtonKind.Secondary ? (hover ? GuiTheme.Accent : GuiTheme.Border) : fill;
        }
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath shape = GuiTheme.RoundedRectangle(bounds, GuiTheme.Radius))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, shape);
            using (var pen = new Pen(border)) g.DrawPath(pen, shape);
        }
        if (Focused && ShowFocusCues)
        {
            var ring = new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f);
            using (GraphicsPath shape = GuiTheme.RoundedRectangle(ring, GuiTheme.Radius - 2))
            using (var pen = new Pen(kind == ButtonKind.Primary ? GuiTheme.PrimaryInk : GuiTheme.Accent, 1.5f))
                g.DrawPath(pen, shape);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

// A text box in a rounded field the same height as the buttons; the border turns accent while it has
// focus. The TextBox itself keeps its name and accessibility, so it's used exactly as before.
internal sealed class FieldPanel : Panel
{
    readonly TextBox box;

    public FieldPanel(TextBox text)
    {
        box = text;
        BackColor = GuiTheme.Surface;
        Height = 36;
        Padding = new Padding(10, 8, 10, 4);
        Margin = new Padding(0, 0, 8, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        text.BorderStyle = BorderStyle.None;
        text.Dock = DockStyle.Fill;
        text.Margin = Padding.Empty;
        text.Font = GuiTheme.Font(10);
        text.BackColor = GuiTheme.Surface;
        text.ForeColor = GuiTheme.Ink;
        Controls.Add(text);
        text.GotFocus += delegate { Invalidate(); };
        text.LostFocus += delegate { Invalidate(); };
        text.EnabledChanged += delegate { BackColor = text.BackColor = text.Enabled ? GuiTheme.Surface : GuiTheme.DisabledSurface; Invalidate(); };
        Click += delegate { text.Focus(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var clear = new SolidBrush(GuiTheme.ContainerBackground(Parent))) g.FillRectangle(clear, ClientRectangle);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath shape = GuiTheme.RoundedRectangle(bounds, GuiTheme.Radius - 2))
        {
            using (var brush = new SolidBrush(BackColor)) g.FillPath(brush, shape);
            using (var pen = new Pen(box.Focused ? GuiTheme.Accent : GuiTheme.Border, box.Focused ? 1.5f : 1f)) g.DrawPath(pen, shape);
        }
    }
}

// A paper-coloured card with rounded corners, for grouping parts of a window.
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        BackColor = GuiTheme.Surface;
        Padding = new Padding(20, 16, 20, 16);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var clear = new SolidBrush(GuiTheme.ContainerBackground(Parent))) g.FillRectangle(clear, ClientRectangle);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath shape = GuiTheme.RoundedRectangle(bounds, GuiTheme.Radius + 2))
        {
            using (var brush = new SolidBrush(GuiTheme.Surface)) g.FillPath(brush, shape);
            using (var pen = new Pen(GuiTheme.Border)) g.DrawPath(pen, shape);
        }
    }
}
