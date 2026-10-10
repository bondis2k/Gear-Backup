using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

partial class GearBackup
{
    static partial class GuiTheme
    {
        sealed class Palette
        {
            public Color Ink, Muted, Accent, AccentDark, AccentSoft, Desktop, Background, Surface, Border, DisabledSurface, DisabledInk;
        }
        static readonly Palette original = new Palette {
            Ink = Color.FromArgb(28, 42, 51), Muted = Color.FromArgb(102, 117, 127),
            Accent = Color.FromArgb(0, 117, 106), AccentDark = Color.FromArgb(0, 94, 85), AccentSoft = Color.FromArgb(226, 242, 239),
            Desktop = Color.FromArgb(243, 245, 247), Background = Color.FromArgb(243, 245, 247), Surface = Color.White,
            Border = Color.FromArgb(221, 227, 232), DisabledSurface = Color.FromArgb(238, 241, 243), DisabledInk = Color.FromArgb(160, 170, 176)
        };
        static readonly Palette retro = new Palette {
            Ink = Color.Black, Muted = Color.FromArgb(64, 64, 64),
            Accent = Color.FromArgb(0, 0, 128), AccentDark = Color.FromArgb(0, 0, 96), AccentSoft = Color.FromArgb(224, 224, 216),
            Desktop = Color.FromArgb(0, 128, 128), Background = Color.FromArgb(192, 192, 192), Surface = Color.FromArgb(255, 255, 240),
            Border = Color.FromArgb(128, 128, 128), DisabledSurface = Color.FromArgb(224, 224, 224), DisabledInk = Color.FromArgb(128, 128, 128)
        };
        static readonly Palette dark = new Palette {
            Ink = Color.FromArgb(232, 238, 241), Muted = Color.FromArgb(168, 183, 193),
            Accent = Color.FromArgb(34, 160, 144), AccentDark = Color.FromArgb(109, 220, 203), AccentSoft = Color.FromArgb(31, 64, 61),
            Desktop = Color.FromArgb(19, 25, 30), Background = Color.FromArgb(26, 34, 40), Surface = Color.FromArgb(32, 42, 49),
            Border = Color.FromArgb(70, 86, 96), DisabledSurface = Color.FromArgb(38, 46, 52), DisabledInk = Color.FromArgb(126, 142, 152)
        };
        static Palette palette = retro;
        public static Color Ink { get { return palette.Ink; } }
        public static Color Muted { get { return palette.Muted; } }
        public static Color Accent { get { return palette.Accent; } }
        public static Color AccentDark { get { return palette.AccentDark; } }
        public static Color AccentSoft { get { return palette.AccentSoft; } }
        public static Color Desktop { get { return palette.Desktop; } }
        public static Color Background { get { return palette.Background; } }
        public static Color Surface { get { return palette.Surface; } }
        public static Color Border { get { return palette.Border; } }
        public static Color DisabledSurface { get { return palette.DisabledSurface; } }
        public static Color DisabledInk { get { return palette.DisabledInk; } }
        public static Color ButtonSurface { get { return palette == retro ? Background : Surface; } }
        public static string ThemeName { get { return palette == dark ? "original-dark" : palette == retro ? "retro" : "original"; } }
        public static Color PrimaryInk { get { return palette == dark ? Desktop : Color.White; } }
        public static void ApplyTo(Control control) { Recolor(control, palette); }

        // Native controls retain assigned colours. Remap those alongside owner-drawn controls,
        // including already-open windows, preserving values, focus and operation state.
        public static void ApplyTheme(string name, Control newlyCreated = null)
        {
            Palette previous = palette;
            palette = name == "original-dark" ? dark : name == "original" ? original : retro;
            var roots = new List<Control>();
            foreach (Form form in Application.OpenForms) roots.Add(form);
            if (newlyCreated != null && !roots.Contains(newlyCreated)) roots.Add(newlyCreated);
            foreach (Control root in roots)
            {
                root.SuspendLayout();
                try { Recolor(root, previous); }
                finally { root.ResumeLayout(); root.Invalidate(true); }
            }
        }
        static Color BackgroundColor(Color color, Palette old)
        {
            if (color.IsEmpty || color.A != 255) return color;
            if (color == old.Surface) return Surface;
            if (color == old.Background) return Background;
            if (color == old.Desktop) return Desktop;
            if (color == old.AccentSoft) return AccentSoft;
            if (color == old.Accent) return Accent;
            if (color == old.AccentDark) return AccentDark;
            if (color == old.Border) return Border;
            if (color == old.DisabledSurface) return DisabledSurface;
            return color;
        }
        static Color ForegroundColor(Color color, Palette old)
        {
            if (color.IsEmpty) return color;
            if (color == (old == dark ? Color.FromArgb(255, 156, 139) : Color.FromArgb(175, 69, 47))) return Danger;
            if (color == old.Ink) return Ink;
            if (color == old.Muted) return Muted;
            if (color == old.Accent) return Accent;
            if (color == old.AccentDark) return AccentDark;
            if (color == old.DisabledInk) return DisabledInk;
            return color;
        }
        static void Recolor(Control control, Palette old)
        {
            // Standard input controls use Windows' field colours; FieldPanel inputs use ours.
            if (!(control is ComboBox) && !(control is NumericUpDown) && (!(control is TextBox) || control.Parent is FieldPanel))
                control.BackColor = BackgroundColor(control.BackColor, old);
            control.ForeColor = ForegroundColor(control.ForeColor, old);
            if (control is ComboBox || control is NumericUpDown || control is ListBox || control is TextBox)
            {
                control.BackColor = palette == dark || control.Parent is FieldPanel ? Surface : SystemColors.Window;
                control.ForeColor = Ink;
            }
            if (control is MainForm) control.BackColor = Desktop;
            var grid = control as DataGridView;
            if (grid != null)
            {
                grid.BackgroundColor = Surface;
                grid.GridColor = Border;
                var cells = new DataGridViewCellStyle(grid.DefaultCellStyle);
                cells.BackColor = Surface; cells.ForeColor = Ink;
                cells.SelectionBackColor = AccentSoft; cells.SelectionForeColor = Ink;
                grid.DefaultCellStyle = cells;
                var alternating = new DataGridViewCellStyle(grid.AlternatingRowsDefaultCellStyle);
                alternating.BackColor = Surface;
                grid.AlternatingRowsDefaultCellStyle = alternating;
                var headers = new DataGridViewCellStyle(grid.ColumnHeadersDefaultCellStyle);
                headers.BackColor = Background; headers.ForeColor = Ink;
                headers.SelectionBackColor = Background; headers.SelectionForeColor = Ink;
                grid.ColumnHeadersDefaultCellStyle = headers;
                RecolorStyle(grid.RowHeadersDefaultCellStyle, old);
                foreach (DataGridViewRow row in grid.Rows)
                {
                    RecolorStyle(row.DefaultCellStyle, old);
                    foreach (DataGridViewCell cell in row.Cells) if (cell.HasStyle) RecolorStyle(cell.Style, old);
                }
            }
            foreach (Control child in control.Controls) Recolor(child, old);
        }
        static void RecolorStyle(DataGridViewCellStyle style, Palette old)
        {
            style.BackColor = BackgroundColor(style.BackColor, old);
            style.ForeColor = ForegroundColor(style.ForeColor, old);
            style.SelectionBackColor = BackgroundColor(style.SelectionBackColor, old);
            style.SelectionForeColor = ForegroundColor(style.SelectionForeColor, old);
        }
    }
}
