using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

// The row of gear icons in the header: Gear Backup itself, then the devices that get a dedicated mode
// (M8, Tracker Mini, OP-XY, OP-1 Field). The highlighted icon is the active mode: it follows the device the
// app has identified, and clicking an icon asks the app to find a device of that type (GearClicked).
// Each icon is a normal button, so keyboard use and accessibility work as usual.
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

    // The mode for a detected device: its own icon, or Gear Backup for no device or one without a mode (the X100VI).
    internal static Gear For(DeviceProfile device)
    {
        return Gears.FirstOrDefault(gear => gear.Device != null && device != null && gear.Device.Id == device.Id) ?? Gears[0];
    }

    // The mode that is showing.
    internal Gear Active { get; private set; }
    internal event Action<Gear> GearClicked;
    // Raised when the active mode changes, whether by a click or by an identified device.
    internal event Action<Gear> ActiveChanged;

    internal GearIconStrip(ToolTip hints)
    {
        Name = "GearIcons"; AccessibleName = "Gear";
        AutoSize = true; WrapContents = false; FlowDirection = FlowDirection.LeftToRight;
        Margin = new Padding(16, 0, 0, 0); Padding = Padding.Empty;
        Active = Gears[0];
        foreach (Gear gear in Gears)
        {
            var button = new GearIconButton(gear) { Name = "GearIcon" + gear.Key, Active = gear == Active };
            hints.SetToolTip(button, gear.Device == null ? "Gear Backup: look for any supported device" : "Look for a connected " + gear.Name);
            button.Click += delegate { var handler = GearClicked; if (handler != null) handler(gear); };
            Controls.Add(button);
        }
    }

    internal void SetActive(Gear gear)
    {
        if (gear == Active) return;
        Active = gear;
        foreach (GearIconButton button in Controls) button.Active = button.Gear == gear;
        var handler = ActiveChanged; if (handler != null) handler(gear);
    }
}

// One picture on a rounded tile: tinted on hover, underlined in the accent colour while it is the active mode.
internal sealed class GearIconButton : Button
{
    readonly Image picture;
    bool active, hover, pressed;
    internal GearIconStrip.Gear Gear { get; private set; }

    internal bool Active
    {
        get { return active; }
        set { if (active != value) { active = value; Invalidate(); } }
    }

    internal GearIconButton(GearIconStrip.Gear gear)
    {
        Gear = gear;
        picture = MainForm.LoadImage(gear.Picture);
        AccessibleName = gear.Name;
        Size = new Size(73, 73); // 40% larger than the first 52px version
        Margin = new Padding(0, 0, 4, 0);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MouseEnter += delegate { hover = true; Invalidate(); };
        MouseLeave += delegate { hover = false; pressed = false; Invalidate(); };
        MouseDown += delegate { pressed = true; Invalidate(); };
        MouseUp += delegate { pressed = false; Invalidate(); };
        EnabledChanged += delegate { Invalidate(); };
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
        bool live = Enabled;
        if (live && (pressed || hover || active))
        {
            var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath shape = GuiTheme.RoundedRectangle(bounds, GuiTheme.Radius))
            using (var brush = new SolidBrush(pressed ? GuiTheme.Background : GuiTheme.AccentSoft)) g.FillPath(brush, shape);
        }
        if (picture != null)
        {
            int side = Math.Min(Width, Height) * 3 / 4;
            var target = new Rectangle((Width - side) / 2, (Height - side) / 2 - 1, side, side);
            if (live) g.DrawImage(picture, target);
            else using (var attributes = new ImageAttributes())
            {
                // Faded while an operation is running, like the disabled buttons.
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.4f });
                g.DrawImage(picture, target, 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        if (active)
        {
            int bar = Math.Max(2, Height / 20);
            using (var brush = new SolidBrush(live ? GuiTheme.Accent : GuiTheme.DisabledInk)) g.FillRectangle(brush, Width / 4, Height - bar - 1, Width / 2, bar);
        }
        if (Focused && ShowFocusCues)
        {
            var ring = new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f);
            using (GraphicsPath shape = GuiTheme.RoundedRectangle(ring, GuiTheme.Radius - 2))
            using (var pen = new Pen(GuiTheme.Accent, 1.5f)) g.DrawPath(pen, shape);
        }
    }
}
