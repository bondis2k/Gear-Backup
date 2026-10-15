using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// The row of gear icons in the header: Gear Backup itself, then the devices that get a dedicated view
// (M8, OP-XY, OP-1 Field). Each icon is a normal button, so keyboard use and accessibility work as usual.
// The icons only announce a click for now; the dedicated views will listen to GearClicked.
internal sealed class GearIconStrip : FlowLayoutPanel
{
    // Entries are the general app first, then devices; Device is null for the app itself.
    internal sealed class Gear
    {
        public string Key, Name, Picture;
        public DeviceProfile Device;
    }

    internal static readonly Gear[] Gears = {
        new Gear { Key = "Backup", Name = AppIdentity.Name, Picture = "Backup-256.png" },
        Of(Devices.M8), Of(Devices.TrackerMini), Of(Devices.OPXY), Of(Devices.OP1Field) };

    static Gear Of(DeviceProfile device)
    {
        return new Gear { Key = device.IconName, Name = device.Name, Picture = device.IconName + "-256.png", Device = device };
    }

    // The gear whose view is showing; only Gear Backup has one today.
    internal string Active { get; private set; }
    internal event Action<Gear> GearClicked;

    internal GearIconStrip(ToolTip hints)
    {
        Name = "GearIcons"; AccessibleName = "Gear";
        AutoSize = true; WrapContents = false; FlowDirection = FlowDirection.LeftToRight;
        Margin = new Padding(24, 0, 0, 0); Padding = Padding.Empty;
        Active = Gears[0].Key;
        foreach (Gear gear in Gears)
        {
            var button = new GearIconButton(gear, gear.Key == Active) { Name = "GearIcon" + gear.Key };
            hints.SetToolTip(button, gear.Device == null ? gear.Name : gear.Name + " (dedicated view coming soon)");
            button.Click += delegate { var handler = GearClicked; if (handler != null) handler(gear); };
            Controls.Add(button);
        }
    }
}

// One picture on a rounded tile: tinted on hover, underlined in the accent colour while it is the active gear.
internal sealed class GearIconButton : Button
{
    readonly Image picture;
    readonly bool active;
    bool hover, pressed;

    internal GearIconButton(GearIconStrip.Gear gear, bool isActive)
    {
        active = isActive;
        picture = MainForm.LoadImage(gear.Picture);
        AccessibleName = gear.Name;
        Size = new Size(52, 52);
        Margin = new Padding(0, 0, 6, 0);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MouseEnter += delegate { hover = true; Invalidate(); };
        MouseLeave += delegate { hover = false; pressed = false; Invalidate(); };
        MouseDown += delegate { pressed = true; Invalidate(); };
        MouseUp += delegate { pressed = false; Invalidate(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && picture != null) picture.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var clear = new SolidBrush(GuiTheme.ContainerBackground(Parent))) g.FillRectangle(clear, ClientRectangle);
        if (pressed || hover || active)
        {
            var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath shape = GuiTheme.RoundedRectangle(bounds, GuiTheme.Radius))
            using (var brush = new SolidBrush(pressed ? GuiTheme.Background : GuiTheme.AccentSoft)) g.FillPath(brush, shape);
        }
        if (picture != null)
        {
            int side = Math.Min(Width, Height) * 3 / 4;
            g.DrawImage(picture, new Rectangle((Width - side) / 2, (Height - side) / 2 - 1, side, side));
        }
        if (active)
        {
            int bar = Math.Max(2, Height / 20);
            using (var brush = new SolidBrush(GuiTheme.Accent)) g.FillRectangle(brush, Width / 4, Height - bar - 1, Width / 2, bar);
        }
        if (Focused && ShowFocusCues)
        {
            var ring = new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f);
            using (GraphicsPath shape = GuiTheme.RoundedRectangle(ring, GuiTheme.Radius - 2))
            using (var pen = new Pen(GuiTheme.Accent, 1.5f)) g.DrawPath(pen, shape);
        }
    }
}
