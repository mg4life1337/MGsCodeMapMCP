# Rolling branch indexing

MGsCodeMapMCP can maintain a current logical index for every repository, branch, and
solution without creating a complete immutable baseline after every pull. The mode is
opt-in, so existing configurations keep their previous commit-baseline behavior.

## Configuration

```json
{
  "dataDirectory": ".\\data",
  "logDirectory": ".\\logs",
  "repositories": [
    {
      "root": "C:\\Source\\SampleRepository",
      "discoverSolutions": true,
      "defaultSolution": "src\\Primary.slnx",
      "indexMode": "rollingBranch",
      "updateStrategy": "incremental",
      "checkAllSolutions": true,
      "skipUnaffectedSolutions": true,
      "servePreviousIndexWhileUpdating": true,
      "autoIndex": true,
      "watchGitHead": true,
      "watchIntervalSeconds": 3,
      "retentionDays": 30,
      "maxRollingBranches": 8,
      "fullRebuildChangeThreshold": 5000
    }
  ]
}
```

All paths in persisted rolling metadata are repository-relative. Index data, impact maps,
branch state, overlays, and logs are written below `dataDirectory` or `logDirectory`. No
`.codemap` or `.codex` directory is created in an analyzed repository.

`repositoryRoots` is an alternative when every repository below one source directory uses
the same settings. If `discoverGitRepositories` and `discoverSolutions` are enabled there,
no matching entry in `repositories` is needed. Use an explicit repository entry when it
needs its own `defaultSolution`.

## Options and migration

| Option | Default | Meaning |
|---|---:|---|
| `defaultSolution` | none | Repository-relative `.sln`/`.slnx` selected when a query omits a solution selector. It is validated at startup and updated first. |
| `indexMode` | `commit` | `rollingBranch` enables branch state and overlays. Omitting it preserves existing behavior. |
| `updateStrategy` | `full` | `incremental` applies the Git delta through `IncrementalCompiler`. |
| `checkAllSolutions` | `true` | Every discovered solution participates in the HEAD impact check. |
| `skipUnaffectedSolutions` | `true` | Avoid Roslyn work for solutions with zero changed inputs. |
| `servePreviousIndexWhileUpdating` | `true` | Keep the last consistent workspace active until a replacement succeeds. |
| `retentionDays` | `30` | Age threshold for inactive branch history and overlays. |
| `maxRollingBranches` | `8` | Number of recent active branch states protected by retention. |
| `fullRebuildChangeThreshold` | `5000` | A larger delta uses a full rebuild with a logged reason. |

No migration is required for an existing `codemap.json`. Add `defaultSolution`,
`indexMode: "rollingBranch"`, and `updateStrategy: "incremental"` to opt in. Existing MCP
tool names and the `repo_path`, `solution_path`, `solution_id`, `commit_sha`, and
`workspace_id` parameters remain supported. Commit-specific baselines, comparisons, and
cleanup tools remain available.

## Solution selection

Queries resolve a solution in this order:

1. explicit `solution_id`;
2. explicit `solution_path`;
3. configured `defaultSolution`;
4. the only registered solution;
5. `AMBIGUOUS_SOLUTION`.

An invalid default path stops discovery with a clear configuration error. An explicit
selector always overrides the default.

## HEAD updates and impact checks

The watcher records HEAD changes without waiting for Roslyn indexing. A latest-only queue
serializes transitions per repository, drops older work that has not started, and always
handles the default solution first. An explicit query for another waiting solution moves it
ahead of the remaining queue.

For a fast-forward from A to B, CodeMap reads the committed Added, Modified, Deleted, and
Renamed paths. For reset or rebase histories it first looks for a merge base. Detached HEAD
uses the existing commit-specific baseline mode. If comparison is unsafe, no compatible
state exists, or the change threshold is exceeded, a full rebuild is performed and the
specific reason is logged.

The persisted impact map models:

```text
solution
  -> projects
      -> source, linked, additional, analyzer, and editor-config inputs
      -> project references and transitive dependents
      -> repository-relative build inputs
```

Changes to solution/project files, C#/VB/F# sources, shared projects,
`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, package
configuration, analyzer inputs, target frameworks, compiler constants, partial classes, and
Designer files invalidate the relevant solution/project graph. Documentation-only changes
skip every solution. A project shared by several solutions marks exactly those solutions.

## Consistency and status

Each update is built in a new shadow workspace. The active solution-specific workspace is
switched only after its overlay batch and metadata are complete. Queries that start while an
update is running keep using the previous workspace. A failed update removes the shadow
workspace and leaves the previous one active.

`repo.status` exposes one entry per rolling solution with its path and ID, branch, HEAD,
indexed commit, baseline, overlay revision, changed files, updated symbols, strategy,
durations, error, and state. States are `up_to_date`, `checking`, `updating`, `stale`,
`failed`, and `full_rebuild_required`. `possibly_stale: true` means the returned index is
internally consistent but may describe the previous successfully indexed commit.

## Storage, checkpointing, and retention

Immutable baselines are shared. Branch state refers to a baseline and a small copy-on-write
overlay; it does not copy the full baseline for a normal pull. Branch names are persisted as
stable SHA-256-derived directory IDs, never as unchecked path segments. A new branch at a
known commit reuses the existing baseline and workspace.

Overlay writes use an atomic batch. When an overlay WAL exceeds 16 MiB or 50,000 records it
is checkpointed into a snapshot; queries continue to use the last active workspace during
that work. Previous branch revisions are retained as small history records so their overlays
can be reclaimed safely. Retention deletes only overlays no longer referenced by any active
or retained state. Immutable baseline cleanup remains an explicit operation through the
existing index cleanup tools.

## Upgrade and troubleshooting

Stop the MCP client, replace the application files, keep `codemap.json`, `data`, and `logs`,
then restart. On the first rolling run an existing compatible commit index can be reused;
otherwise the solution receives one initial full build. Search `repo.status` and the compact
per-solution log lines when an update is skipped or rebuilt. Performance numbers should be
treated as repository-specific and are not claimed unless measured on that repository.
