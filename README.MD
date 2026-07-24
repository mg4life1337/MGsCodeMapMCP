# MGsCodeMapMCP

MGsCodeMapMCP is a public CodeMap fork for semantic navigation of C#, VB.NET, and F# solutions through MCP.

## What is different in this fork?

The central difference is visible at the process boundary:

- `MGsCodeMap.Daemon.exe` is the one long-running, heavy process. It owns Roslyn, MSBuild, repository monitoring, baselines, overlays, and WAL files.
- Any number of MCP clients share that daemon through Streamable HTTP at `http://127.0.0.1:5137/mcp`.
- `MGsCodeMap.TaskHost.exe` is the windowless Windows Task Scheduler action. It remains active while the daemon runs and returns the daemon exit code so Task Scheduler can restart failures.
- `MGsCodeMap.Mcp.exe` is an optional lightweight STDIO-to-HTTP proxy. It does not reference Roslyn, start watchers, open index files, or index code.
- A per-data-directory mutex and lock file prevent a second daemon from opening the same stores.
- Rolling branch indexing, multi-solution discovery, conservative indexing limits, and bounded incremental Solution caching remain available centrally.

This fixes the failure mode where several client tasks each started a complete STDIO server, multiplied Roslyn memory, and competed for the same `overlay.wal`.

```text
Task Scheduler ──> MGsCodeMap.TaskHost.exe ──> MGsCodeMap.Daemon.exe
                                                   ↑
MCP client 1 ─┐                                    │
MCP client 2 ─┼──────── Streamable HTTP ───────────┤
MCP client 3 ─┘                                    ├─ Roslyn / MSBuild
                                                   ├─ repository supervisor
                                                   ├─ baselines and overlays
                                                   └─ one writer per data directory
```

Current fork version: `2.8.0-mgs.7`.

## Windows installation

1. Extract `MGsCodeMapMCP-win-x64.zip` to `C:\Tools\MGsCodeMapMCP`.
2. Copy `codemap.example.json` to `codemap.json` and edit the repository roots.
3. Install the per-user logon task:

```powershell
Set-Location C:\Tools\MGsCodeMapMCP
powershell -ExecutionPolicy Bypass -File .\scripts\install-user-daemon.ps1 -ConfigPath .\codemap.json
```

4. Verify the daemon:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\status-daemon.ps1 -ConfigPath .\codemap.json
Invoke-RestMethod http://127.0.0.1:5137/health
Get-ScheduledTask -TaskName 'MGsCodeMapMCP User Daemon'
Get-ScheduledTaskInfo -TaskName 'MGsCodeMapMCP User Daemon'
```

5. Configure Codex to use HTTP directly:

```toml
[mcp_servers.codemap]
url = "http://127.0.0.1:5137/mcp"
enabled = true
startup_timeout_sec = 30
tool_timeout_sec = 1800
```

6. Restart Codex. All tasks now use the same daemon.

The logon task runs as the signed-in user with `Interactive` logon and limited privileges. It stores no password and keeps the same Git credentials, NuGet settings, certificates, profile environment, and filesystem permissions as interactive development. Its expected state is `Running` while the daemon is alive and `Disabled` after a deliberate stop; `start-daemon.ps1` enables it again.

The scheduled action is the native windowless `MGsCodeMap.TaskHost.exe`, not a persistent PowerShell process. The task host waits for the daemon and returns its exit code. A one-minute Task Scheduler watchdog trigger starts a new task instance after an unexpected exit; `IgnoreNew` makes watchdog ticks no-ops while the healthy task is already running. For interactive diagnostics, run `MGsCodeMap.Daemon.exe --config .\codemap.json --console` directly.

Full instructions: [Windows installation](docs/WINDOWS-INSTALLATION.MD) and [central daemon architecture](docs/CENTRAL-DAEMON.MD).

## Configuration

Minimal `codemap.json`:

```json
{
  "dataDirectory": ".\\data",
  "logDirectory": ".\\logs",
  "logLevel": "Information",
  "server": {
    "transport": "streamableHttp",
    "host": "127.0.0.1",
    "port": 5137,
    "mcpPath": "/mcp",
    "healthPath": "/health",
    "allowRemote": false,
    "singleInstance": true,
    "shutdownTimeoutSeconds": 30
  },
  "repositoryRoots": [
    {
      "path": "C:\\Source",
      "discoverGitRepositories": true,
      "discoverSolutions": true,
      "autoIndex": true,
      "watchGitHead": true,
      "indexMode": "rollingBranch",
      "updateStrategy": "incremental"
    }
  ],
  "indexingResources": {
    "maxConcurrentIndexes": 1,
    "maxParallelProjects": 2,
    "incrementalSolutionCacheSize": 1,
    "incrementalSolutionCacheIdleMinutes": 5,
    "memoryTelemetry": true,
    "releaseMemoryAfterFullIndex": true,
    "memoryReclaimMinimumManagedHeapMb": 768,
    "maxOpenBaselineReaders": 2,
    "maxOpenOverlayReaders": 2,
    "storageReaderIdleSeconds": 60
  }
}
```

Relative `dataDirectory`, `logDirectory`, repository, and solution paths are resolved relative to `codemap.json`.

When `discoverGitRepositories` and `discoverSolutions` are enabled on a root, explicit `repositories` entries are unnecessary unless you need per-repository overrides or an explicit default solution. If several solutions are found, MCP responses expose their stable `solution_id`; pass `solution_path` or `solution_id` where selection is ambiguous. A Solution ID includes the normalized repository root and repository-relative Solution path. Two clones in different folders therefore have separate indexes even when they share the same remote, commit, and `.sln` name.

Full-index memory reclamation is batch-scoped. After publication and the reader idle timeout, the daemon waits until there is no full index, incremental update, or active MCP request and the last MCP request has been quiet for at least 120 seconds. If the managed heap remains above `memoryReclaimMinimumManagedHeapMb`, it requests one LOH compaction and performs one blocking full collection. It does not collect after each Solution.

The default host is loopback-only. A non-loopback host is rejected unless `allowRemote` is explicitly enabled. Authentication and TLS are not built in, so remote exposure should be placed behind an appropriate protected reverse proxy.

## Daemon management

```powershell
.\scripts\start-daemon.ps1   -ConfigPath .\codemap.json
.\scripts\status-daemon.ps1  -ConfigPath .\codemap.json
.\scripts\restart-daemon.ps1 -ConfigPath .\codemap.json
.\scripts\stop-daemon.ps1    -ConfigPath .\codemap.json
```

The stop script requests graceful shutdown and waits up to 30 seconds. `-ForceFallback` is available for a stuck process; it refuses to force-stop while the health response reports an active index publication.

When the scheduled task is installed, `start-daemon.ps1` starts that task instead of detaching a one-shot launcher. `status-daemon.ps1` displays task state, last result, last run, daemon PID, version, health, and observed Solution count.

Uninstalling the logon task does not delete `codemap.json`, `data`, or `logs`:

```powershell
.\scripts\uninstall-user-daemon.ps1 -ConfigPath .\codemap.json
```

## Health and single-instance behavior

`GET http://127.0.0.1:5137/health` reports:

- product version, PID, start time, endpoint, and mode;
- active MCP sessions and request count;
- tracked Solutions, logical workspaces, Roslyn incremental cache entries, and open baseline/overlay readers;
- working set, private bytes, managed heap, and managed-heap fragmentation;
- Solution-cache hits, misses, and evictions;
- repository supervisor and current indexing status.

The endpoint does not return source content, symbols, repository names, or log contents.

The daemon derives a lock identity from the canonical data directory. A second daemon using the same directory exits with code `17` before MSBuild registration, DI construction, store opening, supervisor startup, or WAL access. Different data directories may run independently.

## Optional STDIO compatibility

Use the proxy only when a client cannot connect to Streamable HTTP:

```toml
[mcp_servers.codemap]
command = 'C:\Tools\MGsCodeMapMCP\MGsCodeMap.Mcp.exe'
args = ['--config', 'C:\Tools\MGsCodeMapMCP\codemap.json']
enabled = true
startup_timeout_sec = 30
tool_timeout_sec = 1800
```

The daemon must already be running. Add `--start-daemon` to the proxy arguments if it should attempt to start the sibling daemon and wait briefly for health. Closing proxy stdin ends only the proxy; the daemon remains alive.

## Upgrade from an earlier fork release

1. Stop all earlier MCP host processes.
2. Preserve `codemap.json`, `data`, and `logs`.
3. Replace program files with the new release.
4. Install the user daemon and verify `/health`.
5. Change Codex from `command`/`args` to the HTTP `url` configuration.
6. Restart Codex and confirm one `MGsCodeMap.Daemon.exe` process.

The storage format is unchanged in `2.8.0-mgs.7`; existing baselines, rolling state, overlays, and valid WAL data are reused. A validated compatibility alias reuses an existing `mgs.6` baseline only when its recorded repository root matches the current repository instance. A second clone in another folder receives its own path-scoped index.

## MCP tools

The daemon exposes the existing tool schemas, including:

- `symbols.search`, `symbols.get_card`, `symbols.get_context`
- `code.get_span`, `code.search_text`
- `refs.find`, `graph.callers`, `graph.callees`, `graph.trace_feature`
- `types.hierarchy`, `surfaces.endpoints`, `surfaces.config_keys`, `surfaces.db_tables`
- `workspace.create`, `index.refresh_overlay`, `workspace.reset`, `workspace.list`, `workspace.delete`
- `index.ensure_baseline`, `index.list_baselines`, `index.cleanup`, `index.remove_repo`, `index.diff`
- `repo.status`, `codemap.summarize`, `codemap.export`, `codemap.guide`

Reads from separate HTTP sessions can run concurrently. Full indexing remains bounded process-wide, rolling updates keep latest-only queues, and overlay batches publish atomically so readers continue seeing the last consistent revision during an update.

## Build and test

```powershell
dotnet restore CodeMap.sln
dotnet build CodeMap.sln -c Release --no-restore
dotnet test CodeMap.sln -c Release --no-build --no-restore
powershell -ExecutionPolicy Bypass -File .\scripts\build-win-x64.ps1
```

The Windows archive contains both official executables:

- `MGsCodeMap.Daemon.exe` — central semantic server
- `MGsCodeMap.TaskHost.exe` — windowless supervised Task Scheduler host
- `MGsCodeMap.Mcp.exe` — lightweight compatibility proxy

No executable using an earlier name is included.

## License and attribution

See [LICENSE.MD](LICENSE.MD) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
