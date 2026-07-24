# 2.8.0-mgs.7 Windows memory acceptance

Measured on 2026-07-24 on Windows with 31.84 GB physical memory, .NET 10,
`maxConcurrentIndexes=1`, `maxConcurrentIncrementalSolutions=2`,
`maxParallelProjects=2`, and the default mgs.7 reader/reclaim settings.

## Workload

- Repository discovery root: isolated local multi-repository test root
- Git repositories discovered: 13
- Solutions indexed in the final cold batch: 41
- Cold-batch wall time: 393.9 seconds
- Baseline readers during indexing: at most 2
- Overlay readers during indexing: at most 2
- Fatal indexing errors in the final 41-Solution batch: 0

The earlier reproduced mgs.6 state after 13 representative Solutions was approximately
6.5-7.5 GB working set, 6.7-7.6 GB private bytes, and 4.4 GB managed heap.

## Final memory result

The final mgs.7 process reached a measured process peak working set of 6,467.6
MB while compiling the largest test Solutions. The structured phase telemetry
recorded maxima of 6,288.1 MB working set, 6,128.6 MB private bytes, and 5,007.5
MB managed heap.

After the complete 41-Solution queue became idle, the 60-second reader timeout
expired and the single batch-scoped reclaim ran:

| Metric | Before reclaim | After reclaim | Reduction |
|---|---:|---:|---:|
| Working set | 4,480.0 MB | 704.7 MB | 84.3% |
| Private bytes | 4,352.6 MB | 531.0 MB | 87.8% |
| Managed heap | 2,815.7 MB | 134.3 MB | 95.2% |
| Fragmented bytes | 1,557.9 MB | 2.5 MB | 99.8% |
| Open baseline/overlay readers | 2 / 2 | 0 / 0 | all idle readers closed |

The aggressive, blocking full collection took 267.6 ms. It ran only after
`publishing=false`, `activeFullIndexes=0`, no incremental update, no active MCP
request, and the configured request quiet period.

The result is below all binding idle targets:

- working set <= 2.5 GB: **pass** (0.70 GB)
- private bytes <= 2.0 GB: **pass** (0.53 GB)
- managed heap <= 1.0 GB: **pass** (0.13 GB)

## Repeated full-index behavior

Without restarting the daemon, a temporary local clone of a 14-Solution test
repository was added under a different repository path and fully indexed. The
clone produced 14 independent baselines. Its maximum logged
working set was 5,986.1 MB, below the first batch peak. After its automatic
idle reclaim the process was at 661.8 MB working set, 504.5 MB private bytes,
and 134.4 MB managed heap.

One Windows atomic directory rename was transiently denied after 9 of the 14
Solutions. The remaining Solutions succeeded on retry. The final code retries
only transient `IOException`/`UnauthorizedAccessException` failures while
keeping the publish operation an atomic directory rename. After all 14
baselines were present, the next idle result was 621.0 MB working set, 450.0
MB private bytes, and 96.3 MB managed heap. Neither peak nor idle memory grew
monotonically.

The temporary clone was moved to the Windows Recycle Bin after the test. The
original source worktree remained unchanged.

## Path-scoped duplicate verification

Two local working copies of the same repository, stored in separate test
folders, used the same Git remote and contained the same repository-relative
Solution path. They received distinct Solution IDs.

Each Solution ID combines a hash of the repository-relative Solution path with
a hash of the normalized absolute repository instance path. The two folders
therefore have separate baseline, rolling-state, workspace, and overlay scopes.

## Query and rolling checks after idle close

A previously closed Solution reader reopened without reindexing. Workspace
creation took 13 ms. The following MCP operations succeeded:

- `repo.status`
- `symbols.search` (0.6 ms engine timing)
- `symbols.get_card` (6.4 ms)
- `code.get_span`
- `refs.find` (5.9 ms)
- `graph.trace_feature` (18.4 ms, 16 nodes at depth 2)

For the practical rolling test, one VB file in the temporary clone was edited.
The incremental dependency update completed in 1.11 seconds, reindexed 5
documents, wrote 15 symbols at overlay revision 1, and the added marker method
was immediately found by overlay-aware symbol search. The temporary source
change was then removed.

Multiple MCP sessions were used during the test. A second daemon targeting the
same data directory exited with code 17, and the primary daemon stopped
gracefully through `/shutdown`.

## Verification

The final Release build completed with 0 warnings and 0 errors. The complete
Release test suite passed 1,875 tests with 0 failures and 0 skips; the benchmark
assembly also completed successfully.
