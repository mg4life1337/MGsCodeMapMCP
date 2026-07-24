# 2.8.0-mgs.8 rolling-generation acceptance

Measured on 2026-07-24 on Windows with .NET 10,
`maxConcurrentIndexes=1`, `maxConcurrentIncrementalSolutions=2`, and
`maxParallelProjects=2`.

## Multi-repository workload

- isolated local discovery root
- Git repository instances: 10
- Solutions: 38
- path-scoped active-generation pointers: 10
- complete Solution bindings: 38
- failed repository generations: 0

On an unchanged restart, all 38 bindings used an exact immutable seed:

- reused bindings: 38
- full rebuilds: 0
- incremental refreshes: 0
- exact-seed fallback failures: 0
- slowest individual repository generation: 4.67 seconds
- complete 10-repository startup to ready: 14.88 seconds
- peak working set: 0.98 GiB
- peak private bytes: 1.05 GiB

The same-remote/different-folder regression verifies that each local repository
instance has its own durable active-generation pointer. A separate retention
regression verifies that Solution-state cleanup cannot remove repository-
generation history. Staging recovery coverage verifies that an interrupted
unpublished generation is cleaned without deleting a generation whose atomic
active pointer was already swapped.

## Verification

The Release build completed with 0 warnings and 0 errors. The complete Release
test suite passed 1,901 tests with 0 failures and 0 skips; the benchmark assembly
also completed successfully.
