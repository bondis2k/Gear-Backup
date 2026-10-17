# Tracker compatibility corpus

These are unchanged, publicly licensed original Polyend Tracker saves. They
complement the synthetic layouts in `tests/TrackerTests.cs`. Tests read this
directory; mutations and assembled projects go into the runner's scratch tree.

`sources.json` records upstream URLs, commits, Git blob IDs, byte counts and
SHA-256 hashes for every binary and license. Tests verify sizes and SHA-256
offline. Downloads were also verified against upstream Git blob IDs on import.

## Sources and permissions

- [jaap3/pti-file-format](https://github.com/jaap3/pti-file-format/tree/48dc9978397446296f601077fc1da6a9866a5f88),
  commit `48dc9978397446296f601077fc1da6a9866a5f88`, under the
  [Unlicense](UNLICENSE-jaap3.txt). Its
  [fixture notes](https://github.com/jaap3/pti-file-format/blob/48dc9978397446296f601077fc1da6a9866a5f88/pti-test.md)
  identify Tracker firmware **1.5.0b2**. The audio is a generated 440 Hz sine
  tone; the repository includes its generator.
- [DataGreed/polyendtracker-midi-export](https://github.com/DataGreed/polyendtracker-midi-export/tree/926fb62cbc45f402da73ef2881107f6f0435104a),
  commit `926fb62cbc45f402da73ef2881107f6f0435104a`, under the
  [MIT license](LICENSE-DataGreed.txt), copyright 2022 Alexey Strelkov.
  Its reverse-engineering project and tempo saves carry firmware header
  **1.5.0.255** and structure version **13.13.13.13**.

Upstream paths and names remain in the manifest where local names are shorter.
Keep the license files with these fixtures.

## Expected properties

| Local file | Expected content |
| --- | --- |
| `default.pti` | Mono, 11,025 frames, one-shot mode; 31-character sample name |
| `forward-loop.pti` | Mono, 11,025 frames, forward-loop mode |
| `wavetable.pti` | Mono, 11,025 frames, wavetable mode and flag |
| `granular.pti` | Mono, 11,025 frames, granular mode |
| `tone-10ms.pti` | Mono, 441 frames, granular mode |
| `bpm-131.mt` | Tempo 131 |
| `bpm-40.1.mt` | Tempo approximately 40.1 |
| `chord-arp/project.mt` | Name "1 chord arp test data", tempo 130, playlist containing pattern 1 |
| `chord-arp/patterns/pattern_01.mtp` | Eight tracks, 32 played steps per track, 64 note steps; both effect columns populated |

Pattern track length is stored zero based: byte 31 means 32 played steps.
The upstream [pattern reader](https://github.com/DataGreed/polyendtracker-midi-export/blob/926fb62cbc45f402da73ef2881107f6f0435104a/polytrackermidi/parsers/patterns.py)
independently documents this. The real pattern exposed a last-step omission
that synthetic fixtures had duplicated. Tests also cover stored lengths 0 and
127, invalid lengths and truncated copies of real layouts.

The chord/arp project is intentionally partial: its original instrument is not
included. The missing-instrument test assembles a derived project in scratch
storage using `default.pti`, and changes its final step to reference a missing
slot. Checked-in binaries remain unchanged.

## Coverage limits and extending the corpus

This corpus does not establish compatibility with every Tracker firmware.
Mini/Plus, stereo samples, legacy `.mti` files and newer project structures
still have synthetic coverage rather than redistributable device fixtures.
Add device-saved examples with known firmware, independently recorded expected
values and explicit redistribution permission. Record provenance and hashes;
do not add private songs or unlicensed sample audio.

`tests/TransferFailureTests.cs` uses deterministic MTP fakes. It covers
enumeration and later-transfer disconnects, cancellation without progress,
locked publication, cleanup, lock release, backup isolation and reconnect.
It does not simulate actual USB timing, power loss, or certify devices.

Run from the repository root:

~~~powershell
& tests/run-tests.ps1 -Filter 'Tracker compatibility','MTP failure'
~~~
