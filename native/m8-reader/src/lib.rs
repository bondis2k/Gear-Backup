//! C ABI for Gear Backup: read an M8 file and hand back a JSON summary.
//!
//! The JSON is the contract with the C# side (see `summary.rs` for the schema), so the parser crate's
//! own types never cross the boundary. Every call returns JSON, including failures
//! (`{"schema":1,"error":"..."}`), which keeps the C# side to one code path.

mod summary;

use std::panic::{catch_unwind, AssertUnwindSafe};

/// Bumped whenever an exported function or the JSON schema changes incompatibly.
pub const ABI_VERSION: u32 = 1;

/// Files bigger than this are not M8 songs; refusing early avoids reading something unrelated into memory.
const MAX_FILE_BYTES: u64 = 16 * 1024 * 1024;

#[no_mangle]
pub extern "C" fn m8_abi_version() -> u32 {
    ABI_VERSION
}

/// Reads the song at `path` (NUL-terminated UTF-16) and returns UTF-8 JSON.
/// The buffer must be released with `m8_free(ptr, *out_len)`. Returns null only if `out_len` is null.
///
/// # Safety
/// `path` must point to a NUL-terminated UTF-16 string, and `out_len` to a writable `usize`.
#[no_mangle]
pub unsafe extern "C" fn m8_read_song(path: *const u16, out_len: *mut usize) -> *mut u8 {
    if out_len.is_null() {
        return std::ptr::null_mut();
    }
    let json = if path.is_null() {
        summary::error_json("No file path was given.")
    } else {
        let mut length = 0;
        while *path.add(length) != 0 {
            length += 1;
        }
        let units = std::slice::from_raw_parts(path, length).to_vec();
        catch_unwind(AssertUnwindSafe(|| read_song_json(&units)))
            .unwrap_or_else(|_| summary::error_json("The M8 file reader failed unexpectedly."))
    };
    hand_over(json, out_len)
}

/// Releases a buffer returned by `m8_read_song`.
///
/// # Safety
/// `ptr` and `len` must be exactly what `m8_read_song` returned, and not freed before.
#[no_mangle]
pub unsafe extern "C" fn m8_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)));
    }
}

fn hand_over(json: String, out_len: *mut usize) -> *mut u8 {
    let bytes = json.into_bytes().into_boxed_slice();
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

/// Loads and summarises a song file; the testable core behind `m8_read_song`.
pub fn read_song_json(path_units: &[u16]) -> String {
    let path = path_from_utf16(path_units);
    let size = match std::fs::metadata(&path) {
        Ok(meta) => meta.len(),
        Err(err) => return summary::error_json(&format!("Cannot open the file: {err}")),
    };
    if size > MAX_FILE_BYTES {
        return summary::error_json("The file is too large to be an M8 song.");
    }
    let bytes = match std::fs::read(&path) {
        Ok(bytes) => bytes,
        Err(err) => return summary::error_json(&format!("Cannot read the file: {err}")),
    };
    summary::song_json(&bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().collect()
    }

    #[test]
    fn missing_file_reports_an_error() {
        let json = read_song_json(&wide("does-not-exist.m8s"));
        assert!(json.contains("\"error\""), "{json}");
    }

    #[test]
    fn garbage_reports_an_error_instead_of_panicking() {
        let json = summary::song_json(&[1, 2, 3, 4]);
        assert!(json.contains("\"error\""), "{json}");
    }

    #[test]
    fn exported_functions_round_trip() {
        let path = wide("does-not-exist.m8s");
        let mut with_nul = path.clone();
        with_nul.push(0);
        let mut length = 0usize;
        let ptr = unsafe { m8_read_song(with_nul.as_ptr(), &mut length) };
        assert!(!ptr.is_null() && length > 0);
        let text = unsafe { std::str::from_utf8(std::slice::from_raw_parts(ptr, length)).unwrap().to_owned() };
        assert!(text.contains("\"schema\":1") && text.contains("\"error\""), "{text}");
        unsafe { m8_free(ptr, length) };
    }

    fn fixture(name: &str) -> Vec<u16> {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures/m8").join(name);
        path.to_string_lossy().encode_utf16().collect()
    }

    #[test]
    fn reads_a_real_song() {
        let json: serde_json::Value = serde_json::from_str(&read_song_json(&fixture("TEST-FILE.m8s"))).unwrap();
        let song = &json["song"];
        assert!(json["error"].is_null(), "{json}");
        assert!(song["tempo"].as_f64().unwrap() > 0.0);
        assert_eq!(song["trackRows"].as_array().unwrap().len(), 8);
        assert!(!song["instruments"].as_array().unwrap().is_empty(), "{json}");
    }

    #[test]
    fn an_empty_song_has_no_rows_or_instruments() {
        let json: serde_json::Value = serde_json::from_str(&read_song_json(&fixture("V4EMPTY.m8s"))).unwrap();
        assert_eq!(json["song"]["songRows"], 0, "{json}");
        assert!(json["song"]["instruments"].as_array().unwrap().is_empty(), "{json}");
    }
}
