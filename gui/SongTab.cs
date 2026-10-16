using System;

using System.Drawing;

using System.Globalization;

using System.Linq;

using System.Text;

using System.Threading.Tasks;

using System.Windows.Forms;



// The Song tab: pick an M8 song file and see what is in it. Reading goes through IM8SongReader, so this file

// knows nothing about the native DLL; it only shows an M8SongSummary.

internal sealed class SongTab : IWorkflowTab

{

    readonly IWorkflowTabHost host;

    readonly TextBox songPath = new TextBox { Name = "SongFilePath", ReadOnly = true, AccessibleName = "M8 song file" };

    readonly Button browseSong = GuiTheme.Button("Browse…", false);

    readonly Label songNotice = GuiTheme.Label("Choose an M8 song (.m8s) to see its tempo, tracks and instruments.", 9, GuiTheme.Muted);

    readonly Label songSummary = GuiTheme.Label("", 10, GuiTheme.Ink);

    readonly DataGridView songInstruments = new DataGridView { Name = "SongInstruments", AccessibleName = "Song instruments" };

    Control content;

    int songRead;

    // Tests replace the file dialog.

    internal Func<string> FilePicker { get; set; }



    internal SongTab(IWorkflowTabHost host) { this.host = host; }



    public int ContentHeight { get { return content == null ? 0 : content.Height; } }



    public Control Build()

    {

        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var caption = GuiTheme.Label("M8 song file", 9, GuiTheme.Muted);

        caption.Margin = new Padding(0, 8, 0, 6);

        panel.Controls.Add(caption, 0, 0); panel.SetColumnSpan(caption, 2);

        panel.Controls.Add(new FieldPanel(songPath) { Dock = DockStyle.Fill }, 0, 1);

        browseSong.Name = "BrowseSong"; browseSong.Margin = new Padding(8, 0, 0, 0);

        browseSong.Click += delegate { ChooseSong(); };

        panel.Controls.Add(browseSong, 1, 1);

        songNotice.UseMnemonic = songSummary.UseMnemonic = false; // an & in a song name or message is text, not a shortcut key

        songNotice.Name = "SongNotice"; songNotice.MaximumSize = new Size(750, 0); songNotice.Margin = new Padding(0, 8, 0, 6);

        panel.Controls.Add(songNotice, 0, 2); panel.SetColumnSpan(songNotice, 2);

        songSummary.Name = "SongSummary"; songSummary.MaximumSize = new Size(750, 0); songSummary.Visible = false;

        panel.Controls.Add(songSummary, 0, 3); panel.SetColumnSpan(songSummary, 2);

        BuildInstrumentGrid();

        panel.Controls.Add(songInstruments, 0, 4); panel.SetColumnSpan(songInstruments, 2);

        host.Hints.SetToolTip(songPath, "A Dirtywave M8 song file (.m8s). It is only read, never changed.");

        content = panel;

        return panel;

    }



    void BuildInstrumentGrid()

    {

        var grid = songInstruments;

        GuiTheme.StyleGrid(grid);

        grid.ReadOnly = true; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;

        grid.RowHeadersVisible = false; grid.AutoGenerateColumns = false; grid.Visible = false; grid.MultiSelect = false;

        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize; // the header style has vertical padding

        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        grid.Dock = DockStyle.Top; grid.Height = host.Font.Height * 14; grid.Margin = new Padding(0, 8, 0, 0);

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", Name = "SlotColumn", FillWeight = 8 });

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", Name = "TypeColumn", FillWeight = 20 });

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Name = "NameColumn", FillWeight = 30 });

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sample", Name = "SampleColumn", FillWeight = 42 });

    }



    async void ChooseSong()

    {

        if (host.Busy) return;

        string path;

        if (FilePicker != null) path = FilePicker();

        else using (var picker = new OpenFileDialog { Title = "Choose an M8 song", Filter = "M8 song (*.m8s)|*.m8s|All files (*.*)|*.*",

            CheckFileExists = true, Multiselect = false, RestoreDirectory = true })

            path = picker.ShowDialog(host.Owner) == DialogResult.OK ? picker.FileName : null;

        if (string.IsNullOrEmpty(path)) return;

        songPath.Text = path; host.Hints.SetToolTip(songPath, path);

        songPath.Select(path.Length, 0); songPath.ScrollToCaret();

        int read = ++songRead;

        songNotice.Text = "Reading song…"; ShowSong(null);

        M8SongSummary summary = null; string error = null;

        try { summary = await Task.Run(() => M8SongReaders.Current.Read(path)); }

        catch (M8SongException ex) { error = ex.Message; }

        catch (Exception ex) { error = "Cannot read this song: " + ex.Message; }

        host.RunOnUi(delegate {

            if (read != songRead) return;

            if (summary == null) songNotice.Text = error;

            ShowSong(summary);

        });

    }



    void ShowSong(M8SongSummary song)

    {

        using (host.FreezeRedraw())

        {

            songSummary.Visible = songInstruments.Visible = song != null;

            songNotice.Visible = song == null;

            songInstruments.Rows.Clear();

            if (song != null)

            {

                songSummary.Text = DescribeSong(song);

                foreach (M8InstrumentInfo item in song.Instruments)

                    songInstruments.Rows.Add(item.Slot.ToString("X2", CultureInfo.InvariantCulture), item.Kind, item.Name, item.Sample);

                songInstruments.Height = Math.Min(host.Font.Height * 14, host.Font.Height * 3 + songInstruments.RowTemplate.Height * Math.Max(1, song.Instruments.Count));

            }

            host.RefreshLayout();

        }

    }



    // M8 shows song values in hex (slots, transpose, quantize), so they are shown the same way here.

    internal static string DescribeSong(M8SongSummary song)

    {

        var text = new StringBuilder();

        text.AppendLine((song.Name.Length > 0 ? song.Name : "(unnamed song)") + "  ·  firmware " + song.Firmware + "  ·  " +

            song.Tempo.ToString("0.##", CultureInfo.InvariantCulture) + " BPM");

        text.AppendLine("Transpose " + song.Transpose.ToString("X2", CultureInfo.InvariantCulture) + "  ·  Quantize " +

            song.Quantize.ToString("X2", CultureInfo.InvariantCulture) + "  ·  Song length " + song.SongRows + " rows");

        text.AppendLine(song.Chains + " chains  ·  " + song.Phrases + " phrases  ·  " + song.Tables + " tables  ·  " + song.Instruments.Count + " instruments");

        text.Append("Rows used per track: " + string.Join("  ", song.TrackRows.Select((rows, track) => (track + 1) + ": " + rows)));

        return text.ToString();

    }

}

