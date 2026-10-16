# Gear Backup security audit — 16 October 2026

Audited commit: `878532cf247d59ddaa78a0e073c8d99c1314b3a1` (main, PR #83).

## Remediation in the working tree

The original findings and audit evidence below describe the audited commit. The subsequent implementation addresses findings 1–3 and additional hardening recommendations 1–3:

- Shared restore-destination validation rejects overlapping backups, configured backup repositories and recovery storage, and runs again after plan approval.
- MTP downloads enforce exact byte counts before writes, including empty files, with mirror cleanup on failure.
- ZIP inspection validates actual central-directory records with bounded buffers before constructing `ZipArchive`, including ZIP64 counts/offsets. The directory budget is 32 MB; entry names are limited to 1,024 bytes and per-entry extra data/comments to 4,096 bytes. Pending GUI inspections are cancelled when replaced or when the window closes.
- Metadata readers use one read-locked handle and enforce a running byte budget; line parsing does not require a second whole-file text copy.
- Retention checks both directory file identity and storage metadata before approval, after approval and immediately before recycling, and rechecks latest-backup protection.
- Native reader dependencies load only through System32; builds inspect PE imports and CI exercises rejection of unexpected imports, delay imports and malformed PE data.

Additional recommendation 4 (release signing/provenance/SBOM) is intentionally unchanged at the user's request. Final remediation test results are recorded below after the historical audit results.

This audit combines source review with small, isolated runtime probes. Production code was not modified. Priorities reflect this desktop backup application's data-integrity and availability requirements; no remote-code-execution or privilege-escalation vulnerability was demonstrated.

## Findings requiring changes

### 1. P1 — CLI exact restore can delete its own backup objects

**Evidence:** `src/Restore/Restore.cs:47-82`, compared with the guard at `src/Restore/RestoreCatalog.cs:177`.

The GUI catalog restore path refuses a destination containing the backup or contained by it. The CLI's `RestoreFlow.RunRestore` / `RestoreSelectedBackup` path has no equivalent guard. `Session.RunPrepared` also does not supply one. Selecting a destination inside `.gearbackup/objects` and choosing an exact restore makes the content-addressed objects look like extra target files. Once copying succeeds, the restore deletes those objects.

**Reproduced:** created a one-file backup, passed its objects folder as the CLI restore destination, selected a saved point and exact restore. The original object existed before the operation and was absent afterward; the operation reported `removed=1, exit=0`. Only a disposable audit fixture was affected. A mistaken CLI destination is sufficient; no hostile input is needed. This is a confirmed destructive data-integrity bug.

**Change:** reject equal or overlapping source-backup and destination paths in the shared restore flow, before planning or prompting. Apply the rule consistently to CLI and GUI, preferably through one shared validator. Revalidate before execution. Consider protecting the configured backup repository and recovery storage as destinations as well, so restoring into a sibling backup cannot corrupt it.

**Regression tests:** CLI merge and exact restores must reject the backup root, an ancestor, the objects folder, and another descendant without creating, changing or deleting files. Include the normal `Session.RunPrepared` route and compare behavior with catalog restore.

### 2. P2 — MTP streaming bypasses the advertised size and disk-space budget

**Evidence:** `src/Devices/Mtp/MtpDevice.cs:354-382`; `src/Devices/Mtp/MtpMirror.cs:123-140`.

The space check trusts the device's listed sizes. The real download loop writes until end-of-stream without checking the advertised size. The mirror checks the final size only after download, and skips that check entirely when the advertised size is zero (`item.Size > 0`). A faulty or malicious device can therefore fill the local mirror volume despite passing preflight. Nonzero oversize transfers are rejected too late to prevent resource exhaustion; zero-size transfers are published as successful.

**Reproduced:** the real `MtpDevice.Download` loop, with a managed test resource stream injected into the transport, wrote 65,536 bytes for a declared one-byte object. A separate `IMtpSource` probe announced zero bytes, passed a zero-byte space reservation, transferred 65,536 bytes, and successfully published the file. A declared one-byte mirror transfer wrote all 65,536 bytes before being rejected. The tests deliberately stopped at 64 KiB and did not attempt to fill a disk.

**Change:** validate each read count and use checked arithmetic before writing. Refuse a chunk that would exceed the advertised size. Require exact final equality, including zero. If unknown sizes are legitimately supported, represent them separately from empty files and impose an explicit download budget plus periodic free-space checks. Preserve cancellation and temporary-file cleanup.

**Regression tests:** empty object, zero advertised/nonempty stream, one-byte advertised/oversized stream, premature EOF, invalid read counts, cancellation, and cleanup after an overrun. Assert that bytes written never exceed the allowed budget.

### 3. P2 — ZIP entry limit is applied after the expensive allocation

**Evidence:** `src/Zip/ZipRestore.cs:187-194`; inspection is initiated by `gui/MainForm.ZipRestore.cs:46-71`.

Reading `zip.Entries.Count` materializes the ZIP central directory before comparing its count with `MaxEntries`. The limit therefore stops subsequent application processing, but does not bound the memory or parsing work needed to reach that check. Inspection happens immediately after selecting a ZIP, before device metadata or checksums establish that it is a usable Gear Backup archive. Inspection uses `CancellationToken.None` and runs in the GUI process.

**Reproduced:** lowered the in-memory limit to 10 in the isolated probe, then inspected a ZIP with 20,000 empty entries. Rejection correctly reported 20,000 entries, but the managed heap had already grown by approximately 9.47 MB. The lowered limit demonstrates ordering, not a bypass of the production count value. A production-scale out-of-memory crash was not attempted. Very large filename and extra-field data also deserve bounds.

**Change:** enforce bounds before materializing the archive. Options include a bounded central-directory preflight that validates ZIP/ZIP64 structure and actual record counts, or an archive reader that enforces limits as it parses. A header's declared count alone is not a reliable bound. For stronger containment, run inspection in a memory/time-limited helper process. Add a cancellable inspection context and cancel superseded inspections. Keep the existing extraction-time length and checksum checks.

**Regression tests:** huge entry counts, false declared counts, large names/extra fields, ZIP64, and cancellation. Measure peak memory or enforce a helper-process limit; a test that merely expects the final exception will miss this issue.

## Additional hardening recommendations

These are source-review observations, not additional demonstrated exploits.

1. **Bound metadata reads on the opened handle.** `src/Foundation/Records.cs:19-41` checks `FileInfo.Length`, then separately calls `File.ReadAllText`, `ReadAllLines` or `ReadAllBytes`. Replacement between the check and open can evade the intended limit. Open once with appropriate sharing, validate the opened file, and enforce a running byte limit while reading. Use bounded line parsing for large manifests rather than multiple whole-file copies. A local or shared-storage adversary able to modify metadata is the relevant threat.

2. **Revalidate automatic retention after confirmation.** `src/Catalog/BackupFolders.cs:33-52` scans old folders, asks for confirmation, and recycles the saved paths without checking their identity/content again. The Storage cleanup route already has stronger stamp checks and fresh link checks through `StorageStamp`. Give automatic retention equivalent protection, including a fresh latest-backup check. The exact effects of a junction swap during shell recycling were not reproduced.

3. **Remove the extra DLL dependency search directory.** `src/Devices/M8SongReader.cs:88` uses `SearchDllLoadDir | SearchSystem32`. The first flag allows dependencies beside the native reader, despite the comment saying dependencies are restricted to System32. Because the primary DLL has a full path, use `SearchSystem32` alone if its imports permit it, and check its import table in the build. No exploitable non-system dependency was demonstrated; an attacker already able to replace the primary application DLL can already execute code. Microsoft documents the flag behavior: https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-loadlibraryexa

4. **Keep release provenance on the roadmap.** The repository explicitly documents unsigned binaries. Authenticode signing and a verifiable release provenance/SBOM process would improve distribution assurance. Published SHA-256 files detect corruption only when the reference hash comes from a trusted channel. This is a distribution hardening recommendation, not evidence of a compromised release.

## Controls inspected

- Path canonicalization, traversal/alternate-stream and reserved-name rejection, reparse-point checks, unique temporary files, atomic publication and rollback.
- ZIP inventory and metadata validation, duplicate/path collisions, symlink rejection, size arithmetic, staged checksum verification and device matching.
- Restore planning/execution, recovery copies, exact removal, source/destination isolation and history locking.
- Content-store object handling, manifests, SQLite copy-to-local behavior, bound SQL parameters, defensive/untrusted-schema settings and progress limits.
- MTP enumeration, transfer sizes and mirror cleanup; Fuji PTP bounded payloads and restricted commands.
- XML recipe parsing: DTD prohibition, disabled resolver and document-size limit.
- Rust/C# ABI, bounded native song reads, panic handling, native loading and display-text sanitization.
- Build/installer scripts and CI: least-privilege install, pinned Actions, read-only workflow permissions, disabled checkout credentials, Cargo lockfile use and configured dependency auditing.

This review did not identify a straightforward ZIP traversal, XML external-entity, SQL-injection or command-injection path in the inspected production flows. That statement is limited to the reviewed code and probes.

## Validation and limits

- Security-filtered core suite: **5 passed, 6 failed out of 11**. The six failures occurred while creating required junction/hard-link fixtures (`Access is denied`), including a follow-on missing-path error in fixture cleanup. They do not establish six production vulnerabilities, and those scenarios remain unverified in this sandbox. File-symlink tests did run successfully.
- ZIP restore and faulty-MTP-value tests: **2 passed, 39 assertions**.
- Rust release unit tests: **9 passed, 1 ignored**; the ignored deep-corruption test was then run separately and **passed**.
- Three targeted audit probes reproduced the findings above against a freshly compiled core from the audited checkout.
- No physical-device testing, race stress campaign, actual disk-exhaustion test, production-scale memory-exhaustion test, or fresh vulnerability-database scan was performed. `cargo-audit` is not installed locally; its presence in CI is not a verified clean dependency result. No dependency was installed for this audit.
- The native corruption test is a seeded mutation corpus, not coverage-guided fuzzing or a proof of parser safety.

Probe source: `.test-runs/security-audit-20261016/AuditProbes.cs`.
Probe output: `.test-runs/security-audit-20261016/results.txt`.
These evidence files are inside the repository's ignored test-output directory; preserve them separately if sharing the report.

Recommended order: fix shared restore isolation first, then MTP stream budgets, then ZIP inspection resource containment. Follow with bounded metadata reads and retention revalidation.

## Remediation validation

- Full core suite: **92 tests passed, 1,499 assertions**.
- Full GUI suite: **25 tests passed, 1,297 assertions**, including superseded/closed-window ZIP inspection cancellation.
- Link-dependent suites were rerun outside the restrictive sandbox; the junction/hard-link tests that could not run during the original audit passed. Cleanup of some ignored test fixtures still reported access-denied warnings after the suites passed.
- Native import policy tests passed for the real reader and rejected a planted DLL dependency, unexpected delay imports and truncated PE data. This policy check is included in CI.
- Production CLI and GUI builds succeeded in `dist/security-audit/`; the native reader loaded successfully in the core M8 reader tests with System32-only dependency resolution.
- `git diff --check` passed. No physical MTP hardware, actual disk exhaustion or production-scale memory exhaustion was used; the regression tests exercise bounded versions of those failure conditions.

Regression coverage lives in `tests/AuditRegressionTests.cs`, `tests/GuiRestoreTests.cs`, and `tests/run-native-import-tests.ps1`. The historical audit probes remain unchanged as evidence of the original behavior and are not the regression suite for the fixes.
