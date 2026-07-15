# Changelog

## 2.8.0-mgs.2

- Added opt-in rolling branch indexes backed by shared baselines and atomic overlays.
- Added persisted multi-solution impact maps and unaffected-solution skipping.
- Added validated default-solution routing with explicit-selector precedence.
- Added non-blocking HEAD observation, latest-only repository queues, query-driven solution
  priority, status reporting, safe branch IDs, overlay history, retention, and checkpointing.
- Extended committed Git diffs for rename paths, ancestor checks, and merge-base fallback.
- Corrected incremental C# and VB.NET behavior across dependent projects, deleted/renamed
  files, overlay source locations, cross-project references, callers, and type relations.
- Added rolling configuration, migration, consistency, storage, and upgrade documentation.

The rolling mode is not enabled automatically. Configurations without `indexMode` retain
the existing commit-baseline behavior.
