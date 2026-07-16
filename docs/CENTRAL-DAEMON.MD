# Central daemon architecture

## Why the process model changed

STDIO MCP clients commonly own the lifetime of the command they start. When several client tasks use the same STDIO configuration, each task can therefore start a complete semantic server. Each process then has its own Roslyn and MSBuild state while trying to open the same rolling overlays.

MGsCodeMapMCP `2.8.0-mgs.5` moves all semantic state into one long-lived process:

```text
HTTP session A ─┐
HTTP session B ─┼── /mcp ──> MGsCodeMap.Daemon
HTTP session C ─┘                ├─ RepositorySupervisor
                                ├─ RollingIndexCoordinator
                                ├─ Roslyn / MSBuild Solution cache
                                ├─ baseline readers
                                └─ overlay writer and WAL recovery
```

`MGsCodeMap.Mcp` is a separate compatibility proxy. Its project uses only the .NET base libraries and HTTP client. It has no project reference to CodeMap libraries, Roslyn, MSBuild, storage, query, or supervisor code.

## Startup order

1. Resolve and validate `codemap.json` and canonical data/log directories.
2. Derive the single-instance identity from the canonical data directory.
3. Acquire the named mutex and open `data/daemon.lock`.
4. If acquisition fails, read the existing metadata, probe health, print PID and endpoint, and exit with code 17.
5. Only the lock owner registers MSBuild, builds the dependency container, and resolves stores.
6. Kestrel binds the configured HTTP endpoint.
7. `RepositorySupervisor` starts and schedules the existing latest-only rolling queues.

This order is intentional: a rejected second process never constructs `CustomSymbolStore`, opens a baseline, creates `EngineOverlay`, opens `overlay.wal`, or starts a repository watcher.

## Streamable HTTP transport

The default endpoint is `POST http://127.0.0.1:5137/mcp`.

- A request without `Mcp-Session-Id` creates a session and returns the ID in the response header.
- Later requests send that header to retain session-local defaults.
- `DELETE /mcp` releases a session.
- Notifications return HTTP 202 without a JSON-RPC body.
- JSON responses use `application/json`; clients requesting only `text/event-stream` receive an SSE message.
- Closing or cancelling one HTTP request uses only that request's cancellation token.
- A disconnected client cannot stop the host or another session.

The dispatch code, tools, schemas, handlers, query engine, stores, and rolling implementation are shared with the earlier host. Only transport and process lifetime changed.

## Session isolation

Repository discovery and registered rolling solutions are daemon-wide because they describe shared indexes. Sticky workspace selection is different: the general sticky key includes the current MCP session ID. `workspace.create` in one session therefore cannot redirect omitted `workspace_id` calls in another session. Rolling solution workspaces remain explicit solution-scoped shared state.

Clients that intentionally share a workspace should pass the same `workspace_id` explicitly.

## Concurrency and publication

- Read-only tool calls from separate sessions may execute concurrently.
- The process-wide full-index gate keeps `maxConcurrentIndexes` at its configured bound.
- Incremental compilation serializes mutation of its cached MSBuild Solution.
- Rolling requests retain the existing latest-only per-repository queue.
- An overlay batch writes WAL records and flushes before changing the in-memory revision.
- Queries keep using the last consistent revision while a new one is prepared.

## Single instance and lock metadata

`data/daemon.lock` contains PID, start time, executable, canonical data directory, HTTP endpoints, and version. The file is diagnostic metadata; ownership is decided by the operating-system synchronization primitive and the held file handle, not by file existence alone.

After an unclean exit, the operating system releases ownership. The next owner safely replaces stale metadata. Existing overlay startup still reads the last snapshot and replays valid WAL records until the first incomplete or invalid record.

## Shutdown

`POST /shutdown` changes the daemon to stopping state and stops accepting MCP work. The host then:

1. stops Kestrel;
2. cancels repository observation and rolling queues;
3. disposes the incremental Solution cache;
4. checkpoints non-empty overlays and closes WAL writers;
5. disposes baseline readers;
6. removes its lock metadata and releases the mutex.

Management scripts wait for this sequence. A force fallback is opt-in and refused while health reports an active publication.

## Health and memory

`GET /health` exposes operational values only. It includes process memory, loaded Solution-cache count, cache hits/misses/evictions, open workspace/baseline/overlay counts, active sessions, supervisor status, and indexing activity. It does not expose source text, symbols, repository names, or request payloads.

The central process prevents memory from multiplying per client. Incremental MSBuild Solutions remain bounded by `incrementalSolutionCacheSize` and idle eviction. Full builds remain bounded by `maxConcurrentIndexes` and `maxParallelProjects`. No forced `GC.Collect` is used.

## Endpoint security

With `allowRemote: false`, configuration validation accepts only loopback hosts. The daemon has no built-in remote authentication or TLS termination. Keep the default local binding unless a protected deployment supplies those controls externally.
