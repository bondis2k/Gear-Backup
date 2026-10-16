using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // One Tracker pattern as a table: a row per step, a column per track that has anything in it. Read only.
    internal sealed class TrackerPatternDialog : DialogForm
    {
        static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        internal readonly DataGridView Grid = new DataGridView { Name = "TrackerPatternSteps", AccessibleName = "Pattern steps" };

        internal TrackerPatternDialog(string heading, TrackerPatternFile pattern, IList<string> trackNames)
            : base(heading)
        {
            Accept.Text = "Close"; buttons.Controls.RemoveAt(1); CancelButton = Accept;
            ClientSize = new Size(900, 600);
            GuiTheme.StyleGrid(Grid); Grid.ReadOnly = true; Grid.AllowUserToAddRows = Grid.AllowUserToDeleteRows = false;
            Grid.RowHeadersVisible = false; Grid.MultiSelect = false; Grid.Dock = DockStyle.Fill;
            Grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            Grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            Grid.Font = GuiTheme.Font(9);
            Grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Step", SortMode = DataGridViewColumnSortMode.NotSortable });
            var shown = new List<int>();
            for (int t = 0; t < pattern.Tracks.Count; t++)
                if (HasContent(pattern.Tracks[t])) shown.Add(t);
            int rows = 0;
            foreach (int t in shown)
            {
                string name = trackNames != null && t < trackNames.Count && trackNames[t].Length > 0 ? " " + trackNames[t] : "";
                Grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = (t + 1).ToString(CultureInfo.InvariantCulture) + name, SortMode = DataGridViewColumnSortMode.NotSortable });
                rows = Math.Max(rows, pattern.Tracks[t].Length);
            }
            for (int step = 0; step < rows; step++)
            {
                var cells = new object[shown.Count + 1];
                cells[0] = (step + 1).ToString(CultureInfo.InvariantCulture);
                for (int c = 0; c < shown.Count; c++)
                {
                    TrackerTrack track = pattern.Tracks[shown[c]];
                    cells[c + 1] = step < track.Length ? Cell(track.Steps[step]) : "";
                }
                Grid.Rows.Add(cells);
            }
            var legend = GuiTheme.Label(shown.Count == 0 ? "This pattern has no notes or effects." :
                "Notes are shown as MIDI note names (60 = C4). Instruments: 01-48 sample, M1-M16 MIDI, S1-S3 synth. Effects follow as symbol and value, for example T120 for a tempo of 120. Only tracks with content are shown.",
                9, GuiTheme.Muted);
            legend.MaximumSize = new Size(820, 0);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(Grid, 0, 0); content.Controls.Add(legend, 0, 1);
            layout.Controls.Add(content, 0, 1);
        }

        static bool HasContent(TrackerTrack track)
        {
            for (int i = 0; i < track.Length; i++) if (!track.Steps[i].IsEmpty) return true;
            return false;
        }

        // "C4 03 V80": the note, the instrument and the effects; empty for a step that does nothing.
        internal static string Cell(TrackerStep step)
        {
            if (step.IsEmpty) return "";
            string note = step.Note >= 0 ? NoteNames[step.Note % 12] + (step.Note / 12 - 1).ToString(CultureInfo.InvariantCulture)
                : step.Note == -2 ? "FADE" : step.Note == -3 ? "CUT" : step.Note == -4 ? "OFF" : "";
            string instrument = !step.HasNote ? "" : step.Instrument < 48 ? (step.Instrument + 1).ToString("00", CultureInfo.InvariantCulture)
                : step.Instrument < 64 ? "M" + (step.Instrument - 47).ToString(CultureInfo.InvariantCulture) : "S" + (step.Instrument - 63).ToString(CultureInfo.InvariantCulture);
            return string.Join(" ", new[] { note, instrument, TrackerFx.Text(step.Fx0Type, step.Fx0Value), TrackerFx.Text(step.Fx1Type, step.Fx1Value) }.Where(t => t.Length > 0));
        }
    }
}
