# Rolling branch generations

Rolling indexing treats a branch as the long-lived logical cache key. HEAD and
the index/working-tree fingerprint are internal consistency markers, not
separate logical branches.

## Per-Solution seed selection

An exact target is preferred regardless of age. Otherwise, each Solution
considers its three newest compatible, complete states. A candidate is excluded
when its overlay is missing or its storage schema, extractor version, or
MSBuild fingerprint differs.

Similarity is the weight of identical relevant inputs divided by the weight of
the union of relevant inputs:

| Input | Weight |
|---|---:|
| Solution and repository-wide build inputs | 8 |
| C#, Visual Basic, and F# project files | 5 |
| Source files | 1 |
| Unrelated files | 0 |

The default threshold is `0.60`. A candidate at or above the threshold is
forked and updated incrementally. A candidate below the threshold causes a full
rebuild of that Solution only. Ties prefer an identical commit, then an
ancestor, then a shared merge base, then a divergent commit; fewer changed
projects and newer publication time break remaining ties.

## Publication and query consistency

Seed overlays are immutable. Every target gets an isolated snapshot-consistent
fork. Deletes are computed against the merged seed view, including symbols
introduced by earlier overlay revisions.

A target generation becomes visible only when:

- every configured Solution has a complete binding;
- all incremental validation failures have completed their per-Solution full
  rebuild;
- branch, HEAD, index, and working-tree fingerprint still match the initial
  snapshot; and
- the durable manifest has been flushed and atomically replaced.

If the repository changes during the run, all staging workspaces are discarded
and the newest snapshot is queued. If a Solution fails, the previous generation
remains active and the incomplete generation is not published.
An atomic staging manifest records every target workspace. After an interrupted
process, the next run removes workspaces from an unpublished generation; if the
active pointer was already swapped before the interruption, those workspaces
are retained and only the completed staging marker is cleared.

Manual workspace stickiness is separate from rolling publication. Without an
explicit workspace, queries use only the active generation for the current
target. By default, a previous branch generation is never silently served as
current state.

## Git and concurrency

Repository state and diffs use LibGit2Sharp. Indexing does not invoke
`git.exe`, shell commands, or an implicit fetch. Remote-tracking references may
be used after another process has fetched them locally.

Independent Solutions may update in parallel. The default
`maxConcurrentIncrementalSolutions` is `2`; each Solution remains serialized
against its own cached Roslyn workspace. Full-index concurrency and project
parallelism retain their separate limits.

Operational targets for a representative multi-Solution repository are:

- quick analysis of all Solutions within 5 seconds;
- an identical seeded branch ready within 10 seconds;
- a small warm project change within 30 seconds and a cold change within 60
  seconds; and
- no full rebuild above the similarity threshold unless strict validation
  records a reason.
