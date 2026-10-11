# Local comparison

Baseline: `f8195a4` (0.9.2 before these optimizations). Updated: the working tree for this change. Five measured iterations per scenario, with one discarded warm-up. See the companion environment JSON files and [methodology](../README.md).

| Scenario | Before median ms | After median ms | Before allocated MB | After allocated MB |
| --- | ---: | ---: | ---: | ---: |
| Dashboard, 12 backups / 2,400 files | 74.41 | 40.62 | 8.72 | 0.36 |
| Storage, same history | 70.55 | 75.52 | 8.72 | 8.72 |
| Full backup, 300 × 4 KB files | 1024.06 | 969.47 | 625.44 | 42.04 |
| Unchanged incremental, small files | 442.31 | 207.13 | 12.18 | 6.13 |
| Disk verification, small files | 321.92 | 71.75 | 301.05 | 0.66 |
| Copy/hash, small files in memory | 43.42 | 1.34 | 602.16 | 1.70 |
| Full backup, 64 MB file | 245.18 | 117.10 | 2.20 | 2.20 |
| Unchanged incremental, large file | 80.31 | 27.80 | 0.27 | 0.12 |
| Disk verification, large file | 52.27 | 42.87 | 1.02 | 1.02 |

Allocation is cumulative managed allocation across threads, not peak memory or native allocation. MB uses 1,048,576 bytes. The large-file pipeline is retained; its allocation is unchanged. Small-file copying avoids background-read tasks and repeated large buffers, so the isolated memory scenario shows a much clearer difference than full backups dominated by file creation and writes.

Disk times varied between local runs. The unchanged Storage scanner was about 7% slower in this comparison, illustrating that background I/O, caching and filesystem variability affect timings. Treat elapsed-time ratios as observations for these synthetic fixtures, not promised device throughput. Run on your actual backup drive to evaluate real gains.
