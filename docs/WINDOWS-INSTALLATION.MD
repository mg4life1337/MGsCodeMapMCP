# Windows installation

## Recommended per-user installation

The recommended installation uses Task Scheduler at user logon. The daemon then runs with the same Git credentials, NuGet configuration, certificates, environment variables, temporary directory, MSBuild discovery, and filesystem access as the signed-in developer.

### Copy and paste

```powershell
Set-Location C:\Tools\MGsCodeMapMCP
Copy-Item .\codemap.example.json .\codemap.json
notepad .\codemap.json
powershell -ExecutionPolicy Bypass -File .\scripts\install-user-daemon.ps1 -ConfigPath .\codemap.json
powershell -ExecutionPolicy Bypass -File .\scripts\status-daemon.ps1 -ConfigPath .\codemap.json
Invoke-RestMethod http://127.0.0.1:5137/health
```

Add this to Codex `config.toml`:

```toml
[mcp_servers.codemap]
url = "http://127.0.0.1:5137/mcp"
enabled = true
startup_timeout_sec = 30
tool_timeout_sec = 1800
```

Restart Codex and verify `repo.status`.

## Configuration choices

Use `repositoryRoots` when all repositories beneath one source root should be discovered automatically:

```json
{
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
  ]
}
```

Explicit `repositories` entries are needed only for per-repository settings, a fixed solution list, or an explicit `defaultSolution`.

## Management

All commands accept an absolute or relative configuration path, including paths containing spaces:

```powershell
.\scripts\start-daemon.ps1 -ConfigPath .\codemap.json
.\scripts\status-daemon.ps1 -ConfigPath .\codemap.json
.\scripts\restart-daemon.ps1 -ConfigPath .\codemap.json
.\scripts\stop-daemon.ps1 -ConfigPath .\codemap.json
```

The install operation uses `Register-ScheduledTask -Force`, so running it again updates the same task. The uninstall operation removes only the scheduled task. Neither operation deletes configuration, data, logs, baselines, overlays, or rolling state.

## Upgrade

1. Stop the old host processes and daemon.
2. Back up and preserve `codemap.json`, `data`, and `logs`.
3. Replace application files from the new ZIP.
4. Run `install-user-daemon.ps1` again.
5. Check `/health` and confirm the expected version.
6. Change Codex from STDIO `command`/`args` to the HTTP `url`.
7. Confirm that only one `MGsCodeMap.Daemon.exe` is running.

The `2.8.0-mgs.5` storage format is compatible with the preceding fork release, so existing indexes are retained.

## STDIO fallback

For clients without Streamable HTTP support:

```toml
[mcp_servers.codemap]
command = 'C:\Tools\MGsCodeMapMCP\MGsCodeMap.Mcp.exe'
args = ['--config', 'C:\Tools\MGsCodeMapMCP\codemap.json']
```

This executable is only a proxy. Start the daemon first, or add `--start-daemon` to the proxy arguments.

## Windows service considerations

A registered Windows service often runs under a service identity with different user environment variables, Git credentials, NuGet sources, certificates, network-drive mappings, temporary directories, and MSBuild discovery. This release therefore supplies the logon-task workflow as the supported default and does not install a service automatically.

If a managed deployment wraps the daemon as a service, use a deliberately selected identity, absolute paths, explicit permissions, and the same health and graceful-shutdown checks used by the supplied scripts. Validate repository and package access before enabling automatic indexing.

## Troubleshooting

### Port already in use

Change `server.port` in `codemap.json`, restart the daemon, and update the Codex URL. A clear bind error is written to stderr and the log.

### Exit code 17

Another daemon owns the same canonical data directory. Run `status-daemon.ps1` and use the endpoint shown in `data/daemon.lock`. Do not delete the lock file while that PID is alive.

### Daemon starts but indexing cannot load a solution

Run the daemon interactively with `--console`, verify the selected SDK/MSBuild installation, and confirm that the signed-in user can restore and build the solution.

### High memory during initial indexing

Check `/health`, keep the default indexing limits, and wait for idle Solution eviction. Working set may include memory-mapped baseline pages that the operating system can reclaim. The important process invariant is one heavy daemon regardless of client count.

### Stop timeout

Check whether `indexing.publishing` is true. Wait for publication and retry. Use `-ForceFallback` only after the health check reports no active publication; WAL recovery protects the last valid records after an unclean exit.
