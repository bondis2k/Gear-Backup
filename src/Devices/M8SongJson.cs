using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace GearBackup
{
    // Turns the native reader's JSON (schema 1, documented in native/m8-reader/src/summary.rs) into an M8SongSummary.
    static class M8SongJson
    {
        internal const int Schema = 1;

        internal static M8SongSummary Parse(string json)
        {
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { root = null; }
            catch (InvalidOperationException) { root = null; }
            if (root == null || Int(root, "schema") != Schema) throw new M8SongException("The M8 reader returned an unexpected result.");
            object error;
            if (root.TryGetValue("error", out error)) throw new M8SongException(Convert.ToString(error, CultureInfo.InvariantCulture));
            var song = Object(root, "song");
            if (song == null) throw new M8SongException("The M8 reader returned no song.");
            var result = new M8SongSummary
            {
                Name = Text(song, "name"), Directory = Text(song, "directory"), Firmware = Text(song, "firmware"),
                Tempo = Number(song, "tempo"), Transpose = Int(song, "transpose"), Quantize = Int(song, "quantize"), Key = Int(song, "key"),
                SongRows = Int(song, "songRows"), Chains = Int(song, "chains"), Phrases = Int(song, "phrases"), Tables = Int(song, "tables"),
            };
            Fill(result.TrackRows, List(song, "trackRows"));
            foreach (object item in List(song, "instruments"))
            {
                var instrument = item as Dictionary<string, object>;
                if (instrument == null) continue;
                result.Instruments.Add(new M8InstrumentInfo { Slot = Int(instrument, "slot"), Kind = Text(instrument, "kind"),
                    Name = Text(instrument, "name"), Sample = Text(instrument, "sample") });
            }
            var mixer = Object(song, "mixer");
            if (mixer != null)
            {
                result.MasterVolume = Int(mixer, "master"); result.ChorusVolume = Int(mixer, "chorus");
                result.DelayVolume = Int(mixer, "delay"); result.ReverbVolume = Int(mixer, "reverb");
                Fill(result.TrackVolumes, List(mixer, "tracks"));
            }
            return result;
        }

        static Dictionary<string, object> Object(Dictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        static IEnumerable List(Dictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) && value is IEnumerable && !(value is string) ? (IEnumerable)value : new object[0];
        }

        static string Text(Dictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) && value != null ? Clean(Convert.ToString(value, CultureInfo.InvariantCulture)) : "";
        }

        // Names and paths come from a file anyone can hand you and are shown as they are, so control characters,
        // zero-width characters and bidirectional overrides (which make text read differently from how it is stored) are dropped.
        internal static string Clean(string text)
        {
            var result = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsControl(c) || c == '\u061C' || (c >= '\u200B' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') ||
                    c == '\u2060' || (c >= '\u2066' && c <= '\u2069') || c == '\uFEFF') continue;
                result.Append(c);
            }
            return result.ToString();
        }

        static double Number(Dictionary<string, object> source, string key)
        {
            object value;
            try { return source.TryGetValue(key, out value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0; }
            catch (FormatException) { return 0; }
            catch (InvalidCastException) { return 0; }
        }

        static int Int(Dictionary<string, object> source, string key) { return (int)Math.Round(Number(source, key)); }

        static void Fill(int[] target, IEnumerable values)
        {
            int index = 0;
            foreach (object value in values)
            {
                if (index >= target.Length) break;
                try { target[index] = Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch (FormatException) { } catch (InvalidCastException) { }
                index++;
            }
        }
    }

}
