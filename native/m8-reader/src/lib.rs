//! C ABI for Gear Backup: read an M8 file and hand back a JSON summary.
//!
//! The JSON is the contract with the C# side (see `summary.rs` for the schema), so the parser crate's
//! own types never cross the boundary. Every call returns JSON, including failures
//! (`{"schema":1,"error":"..."}`), which keeps the C# side to one code path.
//!
//! Robustness rules for this boundary:
//! - Nothing unwinds into the caller: every entry point runs under [`guarded`], so a parser panic becomes an
//!   error result.
//! - Input is never trusted: the path comes with an explicit length (no scanning for a terminator), files are
//!   size-limited while reading (not just by a prior `stat`), and only regular files are opened.
//! - Output is always an owned, exactly-sized buffer that the caller returns through [`m8_free`].

#![deny(unsafe_op_in_unsafe_fn)]

mod summary;

use std::fs::File;
use std::io::Read;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;

/// Bumped whenever an exported function or the JSON schema changes incompatibly.
/// 2: `m8_read_song` takes the path length explicitly.
pub const ABI_VERSION: u32 = 2;

/// Files bigger than this are not M8 songs (they are about 100 KB); refusing keeps a wrong pick from filling memory.
const MAX_FILE_BYTES: u64 = 16 * 1024 * 1024;

/// Windows' long-path limit in UTF-16 units; anything longer cannot be a real path.
const MAX_PATH_UNITS: usize = 32_768;

#[no_mangle]
pub extern "C" fn m8_abi_version() -> u32 {
    ABI_VERSION
}

/// Reads the song at `path` (`path_len` UTF-16 units, no terminator needed) and returns UTF-8 JSON.
/// `*out_len` receives the length. Release the buffer with `m8_free(ptr, *out_len)`.
/// Returns null only if `out_len` is null.
///
/// # Safety
/// `path` must point to `path_len` readable `u16`s (or be null), and `out_len` to a writable `usize`.
#[no_mangle]
pub unsafe extern "C" fn m8_read_song(path: *const u16, path_len: usize, out_len: *mut usize) -> *mut u8 {
    if out_len.is_null() {
        return std::ptr::null_mut();
    }
    let json = guarded(|| {
        if path.is_null() || path_len == 0 {
            return summary::error_json("No file path was given.");
        }
        if path_len > MAX_PATH_UNITS {
            return summary::error_json("The file path is too long.");
        }
        // SAFETY: the caller promises `path_len` readable units at `path` (checked non-null above).
        let units = unsafe { std::slice::from_raw_parts(path, path_len) };
        read_song_json(units)
    });
    hand_over(json, out_len)
}

/// Releases a buffer returned by `m8_read_song`. Null is ignored.
///
/// # Safety
/// `ptr` and `len` must be exactly what `m8_read_song` returned, and not freed before.
#[no_mangle]
pub unsafe extern "C" fn m8_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        // SAFETY: `hand_over` produced this pointer from a boxed slice of exactly `len` bytes.
        drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)) });
    }
}

/// Runs `work`, turning a panic into an error result instead of letting it cross the C boundary.
pub(crate) fn guarded(work: impl FnOnce() -> String) -> String {
    catch_unwind(AssertUnwindSafe(work)).unwrap_or_else(|payload| {
        let detail = payload
            .downcast_ref::<&str>()
            .map(|text| text.to_string())
            .or_else(|| payload.downcast_ref::<String>().cloned())
            .unwrap_or_else(|| "unknown cause".to_string());
        summary::error_json(&format!("The M8 file reader failed unexpectedly ({detail})."))
    })
}

fn hand_over(json: String, out_len: *mut usize) -> *mut u8 {
    let bytes = json.into_bytes().into_boxed_slice();
    // SAFETY: `out_len` was checked non-null by the caller of this private helper.
    unsafe { *out_len = bytes.len() };
    Box::into_raw(bytes) as *mut u8
}

fn path_from_utf16(units: &[u16]) -> std::path::PathBuf {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStringExt;
        std::ffi::OsString::from_wide(units).into()
    }
    #[cfg(not(windows))]
    {
        String::from_utf16_lossy(units).into()
    }
}

/// Reads a regular file of at most `MAX_FILE_BYTES`. The limit is enforced on the bytes actually read, so a file
/// that grows after the size check (or reports a wrong size) cannot exceed it.
fn read_limited(path: &Path) -> Result<Vec<u8>, String> {
    let meta = std::fs::metadata(path).map_err(|err| format!("Cannot open the file: {err}"))?;
    if !meta.is_file() {
        return Err("That is not a file.".to_string());
    }
    if meta.len() > MAX_FILE_BYTES {
        return Err("The file is too large to be an M8 song.".to_string());
    }
    let file = File::open(path).map_err(|err| format!("Cannot open the file: {err}"))?;
    let mut bytes = Vec::with_capacity(meta.len() as usize);
    file.take(MAX_FILE_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|err| format!("Cannot read the file: {err}"))?;
    if bytes.len() as u64 > MAX_FILE_BYTES {
        return Err("The file is too large to be an M8 song.".to_string());
    }
    Ok(bytes)
}

/// Loads and summarises a song file; the testable core behind `m8_read_song`.
pub fn read_song_json(path_units: &[u16]) -> String {
    guarded(|| match read_limited(&path_from_utf16(path_units)) {
        Ok(bytes) => summary::song_json(&bytes),
        Err(message) => summary::error_json(&message),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().collect()
    }

    fn parse(json: &str) -> serde_json::Value {
        serde_json::from_str(json).unwrap_or_else(|err| panic!("not JSON ({err}): {json}"))
    }

    fn fixture_path(name: &str) -> std::path::PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures/m8").join(name)
    }

    fn fixture(name: &str) -> Vec<u16> {
        fixture_path(name).to_string_lossy().encode_utf16().collect()
    }

    fn call(path: &[u16]) -> String {
        let mut length = 0usize;
        let ptr = unsafe { m8_read_song(path.as_ptr(), path.len(), &mut length) };
        assert!(!ptr.is_null());
        let text = unsafe { std::str::from_utf8(std::slice::from_raw_parts(ptr, length)).unwrap().to_owned() };
        unsafe { m8_free(ptr, length) };
        text
    }

    #[test]
    fn missing_file_reports_an_error() {
        assert!(read_song_json(&wide("does-not-exist.m8s")).contains("\"error\""));
    }

    #[test]
    fn a_folder_is_not_a_song() {
        let folder: Vec<u16> = Path::new(env!("CARGO_MANIFEST_DIR")).to_string_lossy().encode_utf16().collect();
        assert!(parse(&read_song_json(&folder))["error"].as_str().unwrap().contains("not a file"));
    }

    #[test]
    fn exported_functions_round_trip() {
        let json = parse(&call(&fixture("TEST-FILE.m8s")));
        assert!(json["error"].is_null() && json["song"]["tempo"].as_f64().unwrap() > 0.0, "{json}");
    }

    #[test]
    fn bad_arguments_are_refused_not_dereferenced() {
        let mut length = 0usize;
        assert!(unsafe { m8_read_song(std::ptr::null(), 0, std::ptr::null_mut()) }.is_null());
        let ptr = unsafe { m8_read_song(std::ptr::null(), 5, &mut length) };
        assert!(parse(unsafe { std::str::from_utf8(std::slice::from_raw_parts(ptr, length)).unwrap() })["error"].is_string());
        unsafe { m8_free(ptr, length) };
        let empty = [0u16; 0];
        assert!(parse(&call(&empty))["error"].as_str().unwrap().contains("No file path"));
        let too_long = vec![b'a' as u16; MAX_PATH_UNITS + 1];
        assert!(parse(&call(&too_long))["error"].as_str().unwrap().contains("too long"));
        unsafe { m8_free(std::ptr::null_mut(), 0) }; // freeing null is a no-op
    }

    #[test]
    fn a_panic_becomes_an_error_result() {
        let json = parse(&guarded(|| panic!("boom")));
        assert!(json["error"].as_str().unwrap().contains("boom"), "{json}");
    }

    #[test]
    fn oversized_files_are_refused_without_reading_them() {
        let path = std::env::temp_dir().join(format!("m8-reader-big-{}.m8s", std::process::id()));
        let file = File::create(&path).unwrap();
        file.set_len(MAX_FILE_BYTES + 1).unwrap(); // sparse: costs no disk or time
        drop(file);
        let json = parse(&read_song_json(&path.to_string_lossy().encode_utf16().collect::<Vec<_>>()));
        std::fs::remove_file(&path).ok();
        assert!(json["error"].as_str().unwrap().contains("too large"), "{json}");
    }

    #[test]
    fn reads_a_real_song() {
        let json = parse(&read_song_json(&fixture("TEST-FILE.m8s")));
        let song = &json["song"];
        assert!(json["error"].is_null(), "{json}");
        assert!(song["tempo"].as_f64().unwrap() > 0.0);
        assert_eq!(song["trackRows"].as_array().unwrap().len(), 8);
        assert!(!song["instruments"].as_array().unwrap().is_empty(), "{json}");
    }

    #[test]
    fn an_empty_song_has_no_rows_or_instruments() {
        let json = parse(&read_song_json(&fixture("V4EMPTY.m8s")));
        assert_eq!(json["song"]["songRows"], 0, "{json}");
        assert!(json["song"]["instruments"].as_array().unwrap().is_empty(), "{json}");
    }

    // Whatever the bytes, the answer is valid schema-1 JSON with either a song or an error, and nothing panics
    // out of the reader. Truncations and seeded random corruption of real songs stand in for damaged SD cards.
    #[test]
    fn damaged_songs_never_crash_the_reader() {
        damage_songs(150, 0x9E37_79B9_7F4A_7C15);
    }

    // Many more seeded rounds; run with `cargo test --release -- --ignored` (CI does).
    #[test]
    #[ignore]
    fn damaged_songs_deep_fuzz() {
        for seed in [0x1234_5678_9ABC_DEF1u64, 0xDEAD_BEEF_CAFE_F00D, 0x0BAD_C0DE_1357_9BDF] {
            damage_songs(4000, seed);
        }
    }

    fn damage_songs(rounds: usize, seed: u64) {
        let mut state = seed;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        let mut readable = 0;
        let mut refused = 0;
        for name in ["TEST-FILE.m8s", "V4EMPTY.m8s"] {
            let original = std::fs::read(fixture_path(name)).unwrap();
            let mut cases: Vec<Vec<u8>> = [0, 1, 13, 14, 15, 100, original.len() / 2, original.len() - 1]
                .iter()
                .map(|&cut| original[..cut].to_vec())
                .collect();
            for _ in 0..rounds {
                let mut bytes = original.clone();
                for _ in 0..1 + next() % 16 {
                    let at = (next() % bytes.len() as u64) as usize;
                    bytes[at] = next() as u8;
                }
                cases.push(bytes);
            }
            for bytes in cases {
                let json = parse(&summary::song_json(&bytes));
                assert_eq!(json["schema"], 1);
                assert!(json["song"].is_object() != json["error"].is_string(), "{json}");
                if json["song"].is_object() {
                    readable += 1;
                    assert_eq!(json["song"]["trackRows"].as_array().unwrap().len(), 8);
                } else {
                    refused += 1;
                    let message = json["error"].as_str().unwrap();
                    assert!(!message.contains("unexpectedly"), "the parser panicked: {message}");
                }
            }
        }
        assert!(readable > 0 && refused > 0, "{readable} readable, {refused} refused");
    }
}
