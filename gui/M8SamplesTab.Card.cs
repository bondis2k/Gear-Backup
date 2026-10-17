using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    internal sealed partial class M8SamplesTab
    {
        readonly Button checkAll = GuiTheme.Button("Check all songs on card", false);
        readonly DataGridView cardSongs = new DataGridView { Name = "M8CardSongs", AccessibleName = "Songs checked on M8 card", Visible = false };
        M8CardReport cardReport;

        void BuildCardCheck(TableLayoutPanel panel, FlowLayoutPanel actions)
        {
            checkAll.Name = "CheckAllM8Songs";
            actions.Controls.Add(checkAll);
            checkAll.Click += delegate { RunCardCheck(); };
            host.Hints.SetToolTip(checkAll, "Check every .m8s on the selected card, including subfolders and bundles. Only checks the card; does not search backups.");
            ConfigureGrid(cardSongs, new[] { "Song on card", "References", "Missing", "Result" }, host.Font.Height * 9);
            cardSongs.Columns[0].FillWeight = 220;
            // SelectionChanged can run before CurrentRow changes, which would search the previous song.
            cardSongs.CurrentCellChanged += delegate { ShowCardSong(); };
            cardSongs.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) {
                if (e.RowIndex < 0 || work.IsRunning || host.Busy) return;
                var selected = cardSongs.Rows[e.RowIndex].Tag as M8CardSongResult;
                if (selected == null) return;
                string path = Path.Combine(card.Text, selected.Relative);
                song.Text = path;
                Run();
            };
            panel.Controls.Add(cardSongs);
        }

        void SetChecking(bool active)
        {
            check.Enabled = checkAll.Enabled = song.Enabled = card.Enabled = backups.Enabled = search.Enabled = !active;
            cardSongs.Enabled = !active;
            cancel.Enabled = active;
        }

        void ShowCardSong()
        {
            samples.Rows.Clear(); matches.Rows.Clear(); Report = null; checkedConfig = null;
            var selected = cardSongs.CurrentRow == null ? null : cardSongs.CurrentRow.Tag as M8CardSongResult;
            Report = selected == null ? null : selected.Samples;
            if (Report != null)
                foreach (var sample in Report.Samples)
                {
                    int row = samples.Rows.Add(sample.Slots, sample.Reference, sample.CardStatus, "Not searched");
                    samples.Rows[row].Tag = sample;
                    samples.Rows[row].Cells[1].ToolTipText = sample.Relative == null ? sample.CardStatus : "Card-relative path: " + sample.Relative;
                }
            string detail = selected == null ? "" : selected.Error ?? (Report == null ? "" : string.Join(Environment.NewLine, Report.Warnings));
            warnings.Text = (cardReport == null ? "" : string.Join(Environment.NewLine, cardReport.Warnings)) +
                (detail.Length == 0 ? "" : Environment.NewLine + detail);
            warnings.Visible = warnings.Text.Length != 0;
            if (search.Checked && !work.IsRunning && cardReport != null) SearchSelectedCardSong();
        }

        void SearchSelectedCardSong()
        {
            var selected = cardSongs.CurrentRow == null ? null : cardSongs.CurrentRow.Tag as M8CardSongResult;
            if (selected == null || selected.Error != null)
            {
                notice.Text = "Select a readable song in the card list to search backup histories.";
                return;
            }
            Run(true, Path.Combine(card.Text, selected.Relative));
        }

        async void RunCardCheck()
        {
            if (closed || host.Busy || work.IsRunning) return;
            ClearResults();
            string root = card.Text;
            if (string.IsNullOrWhiteSpace(root)) { notice.Text = "Choose the M8 card root first."; return; }
            var cancellation = work.Begin();
            SetChecking(true);
            notice.Text = "Finding all M8 songs on the card…";
            var reader = M8SongReaders.Current;
            var update = Stopwatch.StartNew();
            try
            {
                var result = await Task.Run(() => {
                    using (new OperationContext(interaction, cancellation.Token).Activate())
                        return M8CardCheck.Run(root, reader, (count, path) => {
                            if (update.ElapsedMilliseconds < 150) return;
                            update.Restart();
                            host.RunOnUi(delegate { if (cancellation.CanPublish) notice.Text = "Checked " + count + " songs: " + M8SongJson.Clean(path); });
                        });
                });
                host.RunOnUi(delegate {
                    if (!cancellation.CanPublish) return;
                    foreach (var item in result.Songs)
                    {
                        string status = item.Error != null ? "Cannot read song" : item.Missing > 0 ? "Missing samples" : item.NeedsAttention ? "Needs attention" : item.Samples.Samples.Count == 0 ? "No sample references" : "All samples present";
                        int row = cardSongs.Rows.Add(M8SongJson.Clean(item.Relative), item.Samples == null ? "—" : item.Samples.Samples.Count.ToString(), item.Error == null ? item.Missing.ToString() : "—", status);
                        cardSongs.Rows[row].Tag = item;
                    }
                    cardReport = result;
                    cardSongs.Visible = true;
                    notice.Text = result.Songs.Count + " song(s) checked; " + result.Songs.Count(s => s.Missing > 0) + " with missing samples (" + result.Songs.Sum(s => s.Missing) +
                        " references); " + result.Songs.Count(s => s.NeedsAttention) + " needing attention. " +
                        (result.Warnings.Count == 0 ? "" : "Scan incomplete: see " + result.Warnings.Count + " scan warning(s) below. ") +
                        "Select a song for sample details; double-click to check that song with the backup-search setting. Unused sampler slots are included.";
                    ShowCardSong();
                });
            }
            catch (OperationCanceledException) { host.RunOnUi(delegate { if (cancellation.IsCurrent) notice.Text = "Card check cancelled. Run again for complete results."; }); }
            catch (Exception ex) { host.RunOnUi(delegate { if (cancellation.IsCurrent) notice.Text = "Cannot check card: " + ex.Message; }); }
            finally
            {
                cancellation.Dispose();
                host.RunOnUi(delegate {
                    if (cancellation.IsCurrent)
                    {
                        if (cancellation.IsCancellationRequested) notice.Text = "Card check cancelled. Run again for complete results.";
                        SetChecking(false); host.RefreshLayout();
                    }
                });
            }
        }
    }
}
