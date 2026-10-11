# Performance benchmarks

From the repository root, run:

```powershell
pwsh -File benchmarks/run-benchmarks.ps1 -Iterations 5 -Output benchmarks/results/after.csv
pwsh -File benchmarks/compare-results.ps1 -Before benchmarks/results/before.csv -After benchmarks/results/after.csv
```

Requires the same Visual Studio C# compiler as the app, plus PowerShell 7. No connected device is used. Fixtures are generated under `.test-runs`, removed after a successful run and retained after a failure. Allow roughly `(Iterations + 1) × 66 MB` plus filesystem metadata (about 450 MB for five iterations). Tests supply a silent interaction layer; no notifications, PATH changes, installed settings or user backup locations are used.

Pass `-SourceRef <commit-or-tag>` to benchmark a previous revision without checking it out or changing your files. The runner copies its tracked core/test sources into an isolated build folder and compiles the same benchmark against them. Versions without the metadata-only Dashboard scanner use the previous full scanner for that scenario. A companion `.environment.json` records the revision, runtime, OS, processor and iteration count.

Scenarios cover full and unchanged incremental backups of 300 small files (4 KB each) and a 64 MB WAV-shaped fixture, disk-read verification of both, and Dashboard/Storage scans of 12 backups containing 200 files each. An in-memory small-file copy/hash scenario separates allocation and task overhead from disk-write variability. These are synthetic workloads, not camera or MTP transfer benchmarks.

Each scenario gets one discarded warm-up, followed by the requested iterations. CSV rows record elapsed milliseconds, managed allocation across all threads and generation-2 collection counts. Managed allocation excludes native verification buffers, and is total allocation rather than peak memory. File contents may remain cached; results do not represent cold-drive performance. No integrity or safety checks are disabled in the measured production routines.

For comparisons, run before and after on the same machine and drive with other builds, tests and apps idle. Prefer at least five iterations and compare medians. Do not impose absolute timing assertions in correctness tests: storage, antivirus, caching and background load affect results. Large-file copying should be watched for regressions as well as small-file improvements.

The committed baseline and optimized CSVs record the local comparison for this change. They are evidence for this workload, not a guarantee for USB cards, spinning disks or network shares.
