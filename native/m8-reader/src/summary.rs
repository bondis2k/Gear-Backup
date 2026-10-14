//! The JSON summary of a song. This is Gear Backup's own schema, deliberately independent of the parser
//! crate's types, so a crate upgrade can't change what the C# side receives.
//!
//! ```text
//! { "schema": 1, "song": {
//!     "name", "directory", "firmware": "6.2.0", "tempo", "transpose", "quantize", "key",
//!     "songRows": <rows of the song grid in use>,
//!     "trackRows": [<used rows per track>; 8],
//!     "chains", "phrases", "tables": <non-empty counts>,
//!     "instruments": [ { "slot", "kind", "name", "sample" } ],
//!     "mixer": { "master", "tracks": [8], "chorus", "delay", "reverb" } } }
//! { "schema": 1, "error": "..." }
//! ```

use m8_file_parser::{Instrument, Song, SongSteps};
use serde_json::{json, Value};

const SCHEMA: u32 = 1;
const EMPTY_STEP: u8 = 0xFF;

pub fn error_json(message: &str) -> String {
    json!({ "schema": SCHEMA, "error": message }).to_string()
}

pub fn song_json(bytes: &[u8]) -> String {
    // The parser indexes straight into the file's contents and panics on a truncated or damaged file (found by
    // the damaged-song test). That is an ordinary "not a readable song" answer, not an application failure.
    let parsed = std::panic::catch_unwind(|| Song::read(&mut std::io::Cursor::new(bytes)));
    match parsed {
        Ok(Ok(song)) => crate::guarded(|| json!({ "schema": SCHEMA, "song": describe(&song) }).to_string()),
        Ok(Err(err)) => error_json(&format!("Not a readable M8 song: {}", err.0)),
        Err(_) => error_json("Not a readable M8 song: the file is damaged or cut short."),
    }
}

fn describe(song: &Song) -> Value {
    let track_rows: Vec<usize> = (0..SongSteps::TRACK_COUNT)
        .map(|track| {
            (0..SongSteps::ROW_COUNT)
                .filter(|row| song.song.steps[row * SongSteps::TRACK_COUNT + track] != EMPTY_STEP)
                .count()
        })
        .collect();
    let song_rows = (0..SongSteps::ROW_COUNT)
        .rev()
        .find(|row| {
            let start = row * SongSteps::TRACK_COUNT;
            song.song.steps[start..start + SongSteps::TRACK_COUNT].iter().any(|step| *step != EMPTY_STEP)
        })
        .map_or(0, |row| row + 1);
    let mixer = &song.mixer_settings;
    json!({
        "name": song.name,
        "directory": song.directory,
        "firmware": song.version.to_string(),
        "tempo": if song.tempo.is_finite() { song.tempo } else { 0.0 },
        "transpose": song.transpose,
        "quantize": song.quantize,
        "key": song.key,
        "songRows": song_rows,
        "trackRows": track_rows,
        "chains": song.chains.iter().filter(|chain| !chain.is_empty()).count(),
        "phrases": song.phrases.iter().filter(|phrase| !phrase.is_empty()).count(),
        "tables": song.tables.iter().filter(|table| !table.is_empty()).count(),
        "instruments": instruments(song),
        "mixer": {
            "master": mixer.master_volume,
            "tracks": mixer.track_volume,
            "chorus": mixer.chorus_volume,
            "delay": mixer.delay_volume,
            "reverb": mixer.reverb_volume,
        },
    })
}

fn instruments(song: &Song) -> Vec<Value> {
    song.instruments
        .iter()
        .enumerate()
        .filter(|(_, instrument)| !instrument.is_empty())
        .map(|(slot, instrument)| {
            json!({
                "slot": slot,
                "kind": kind(instrument),
                "name": instrument.name().unwrap_or(""),
                "sample": match instrument { Instrument::Sampler(sampler) => sampler.sample_path.as_str(), _ => "" },
            })
        })
        .collect()
}

fn kind(instrument: &Instrument) -> &'static str {
    match instrument {
        Instrument::WavSynth(_) => "WavSynth",
        Instrument::MacroSynth(_) => "MacroSynth",
        Instrument::Sampler(_) => "Sampler",
        Instrument::MIDIOut(_) => "MIDI Out",
        Instrument::FMSynth(_) => "FM Synth",
        Instrument::HyperSynth(_) => "HyperSynth",
        Instrument::External(_) => "External",
        Instrument::None => "Empty",
    }
}
