# Changelog

## 2.8.0-mgs.6 - 2026-07-16

### Windowless supervised user task

- Added `MGsCodeMap.TaskHost.exe`, a lightweight Windows `WinExe` that starts the daemon without a console window, remains active for the complete daemon lifetime, and returns the daemon exit code to Task Scheduler.
- Changed the user-logon task to run the task host with `Interactive` logon, limited privileges, no stored password, unlimited execution time, battery-safe settings, and automatic restart after failure.
- Made installation upgrade-safe by gracefully stopping an existing daemon before replacing and restarting the scheduled task.
- Updated start, stop, restart, status, and uninstall scripts to manage the scheduled task while retaining the standalone windowless and interactive console paths.
- Added Task Host dependency/version tests and an isolated practical Task Scheduler acceptance test covering task state, windowless execution, health, solution discovery, crash restart, normal restart, stop, uninstall, and data preservation.
- Updated Windows installation, daemon architecture, troubleshooting, upgrade, and release-package documentation.

## 2.8.0-mgs.5 - 2026-07-16

### Central multi-client daemon

- Split the heavy host into `MGsCodeMap.Daemon.exe` and the lightweight `MGsCodeMap.Mcp.exe` STDIO-to-HTTP proxy.
- Added a loopback Streamable HTTP MCP endpoint with independent sessions, concurrent reads, transport cancellation, and unchanged tool schemas.
- Added per-session sticky workspace state so one client cannot change another client's default workspace.
- Added a health endpoint with process, memory, cache, supervisor, session, workspace, and indexing status.
- Added a per-data-directory named mutex plus lock metadata. A second writer exits with code 17 before opening stores or WAL files.
- Added graceful HTTP shutdown, clean disposal of cached baselines and overlays, and existing WAL replay on restart.
- Added per-user logon-task installation and start, stop, restart, status, and uninstall scripts.
- Added HTTP, three-session, single-instance, proxy-dependency, configuration, and session-isolation tests.
- Updated Windows, cross-platform, Docker, NuGet, documentation, examples, and release packaging for both executable roles.
- Removed the forced full garbage collection after Roslyn pass 2; cache bounds and object lifetime now provide the memory controls.

## 2.8.0-mgs.4

- Bounded full baseline builds to one concurrent index and Roslyn reference extraction to two
  projects by default; discovery and solution-impact checks remain independent.
- Reduced peak allocations by streaming source content, dictionary values, search postings,
  adjacency postings, edges, and facts directly into immutable baseline segments.
- Limited the incremental workspace cache to one solution with five-minute idle eviction.
- Added configurable indexing resource limits and optional peak working-set, private-memory,
  managed-heap, and phase telemetry without repository names or paths in memory log entries.
- Renamed the native host output to `MGsCodeMap.Mcp` on every supported platform. Windows
  releases no longer include either legacy executable name; MCP clients must update `command`.
- Added resource-limit, concurrency, cache eviction, storage parity, executable archive, and
  large-solution measurement coverage.
- Measured a 4.60 GiB median peak working set across three cold large VB.NET runs, about 49%
  below the previously observed 9 GiB, while preserving 164,162 symbols and 1,001,683 references.

## 2.8.0-mgs.3

- Added Roslyn change classification for semantic no-ops, method bodies, project-local API,
  public API, type hierarchy, and structural changes.
- Limited rolling extraction to the affected document, project, or transitive dependency
  scope, with conservative full-scope fallback when baseline content is unavailable.
- Added a bounded per-solution LRU workspace cache and structured per-stage update metrics.
- Canonicalized repository paths at one boundary and removed filename-only source fallbacks.
- Made overlay search and browse merge by symbol identity before limits and counts; overlay
  results win, overloads remain distinct, and equal identities from different projects are
  preserved independently.
- Replaced prior per-file overlay symbols, tokens, references, type relations, and facts on
  repeated revisions, including WAL recovery.
- Served workspace source directly from the current working tree so trivia-only edits remain
  visible without a semantic reindex.
- Derived repository-relative identities from the Git working-tree root even when a solution
  is stored in a nested directory; linked sources outside that root are skipped safely.
- Added C# and VB.NET classifier coverage, focused merge regressions, repeated-overlay tests,
  and isolated VB rolling integration tests.

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
