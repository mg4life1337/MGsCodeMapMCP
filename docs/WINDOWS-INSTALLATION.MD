# Windows installation

## Recommended per-user installation

The recommended installation uses Task Scheduler at user logon. The daemon then runs with the same Git credentials, NuGet configuration, certificates, environment variables, temporary directory, MSBuild discovery, and filesystem access as the signed-in developer. No password is stored and no elevated principal is used.

### Copy and paste

Download the Windows x64 release ZIP, extract it to a directory such as `C:\Tools\MGsCodeMapMCP`, then create the local configuration:

```powershell
Set-Location C:\Tools\MGsCodeMapMCP
Copy-Item .\codemap.example.json .\codemap.json
notepad .\codemap.json
powershell -ExecutionPolicy Bypass -File .\scripts\install-user-daemon.ps1 -ConfigPath .\codemap.json
powershell -ExecutionPolicy Bypass -File .\scripts\status-daemon.ps1 -ConfigPath .\codemap.json
Invoke-RestMethod http://127.0.0.1:5137/health
Get-ScheduledTask -TaskName 'MGsCodeMapMCP User Daemon'
Get-ScheduledTaskInfo -TaskName 'MGsCodeMapMCP User Daemon'
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

## Task behavior

The standard task name is `MGsCodeMapMCP User Daemon`. Installation registers it with:

- an at-logon trigger for the installing user;
- `Interactive` logon and `Limited` run level;
- no stored or requested password;
- start-when-available behavior for a missed logon trigger;
- start and continued execution on battery power;
- unlimited execution time;
- up to three configured restart attempts plus a one-minute watchdog trigger after a failure;
- `Register-ScheduledTask -Force` so the same command updates an existing installation.

The action is `MGsCodeMap.TaskHost.exe`. It is a windowless `WinExe`, starts the daemon without a console, remains active for the daemon's complete lifetime, and returns the daemon exit code. The expected task state is therefore `Running` while `/health` is available. A one-minute watchdog trigger creates a new task instance after an unexpected exit; `IgnoreNew` suppresses watchdog ticks while the task is healthy. `stop-daemon.ps1` disables the task before graceful shutdown, so its expected state after a deliberate stop is `Disabled`. `start-daemon.ps1` enables it again.

Inspect the registered identity, action, state, and last result:

```powershell
$task = Get-ScheduledTask -TaskName 'MGsCodeMapMCP User Daemon'
$task | Select-Object TaskName, State
$task.Principal | Select-Object UserId, LogonType, RunLevel
$task.Actions | Select-Object Execute, Arguments, WorkingDirectory
Get-ScheduledTaskInfo -TaskName 'MGsCodeMapMCP User Daemon' |
    Select-Object LastRunTime, LastTaskResult, NextRunTime
```

Do not use `Stop-ScheduledTask` as the normal shutdown command because it cannot coordinate an active index publication. Use `stop-daemon.ps1`, which requests `/shutdown`, waits for store disposal and lock release, and only permits a controlled force fallback after checking publication state.

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

Keep the default memory controls unless measurement shows a reason to tune them:

```json
{
  "indexingResources": {
    "releaseMemoryAfterFullIndex": true,
    "memoryReclaimMinimumManagedHeapMb": 768,
    "maxOpenBaselineReaders": 2,
    "maxOpenOverlayReaders": 2,
    "storageReaderIdleSeconds": 60
  }
}
```

Reader limits are process-wide. Active queries hold leases, so their readers cannot be closed. Idle overlay close retains snapshots and WAL data and the next query reopens them transparently.

## Management

All commands accept an absolute or relative configuration path, including paths containing spaces:

```powershell
.\scripts\start-daemon.ps1 -ConfigPath .\codemap.json
.\scripts\status-daemon.ps1 -ConfigPath .\codemap.json
.\scripts\restart-daemon.ps1 -ConfigPath .\codemap.json
.\scripts\stop-daemon.ps1 -ConfigPath .\codemap.json
```

Relative paths are resolved from the shell's current directory; absolute paths work unchanged. Quoted paths containing spaces are passed to Task Scheduler and the task host without string reconstruction.

For interactive diagnostics without changing the installed task:

```powershell
.\MGsCodeMap.Daemon.exe --config .\codemap.json --console
```

Stop the scheduled instance first so the single-instance lock is available.

The install operation uses `Register-ScheduledTask -Force`, so running it again updates the same task. The uninstall operation removes only the scheduled task. Neither operation deletes configuration, data, logs, baselines, overlays, or rolling state.

## Upgrade

1. Stop the old host processes and daemon.
2. Back up and preserve `codemap.json`, `data`, and `logs`.
3. Replace application files from the new ZIP.
4. Run `install-user-daemon.ps1` again.
5. Check `/health` and confirm the expected version.
6. Change Codex from STDIO `command`/`args` to the HTTP `url`.
7. Confirm that only one `MGsCodeMap.Daemon.exe` is running.

The baseline storage format in `2.8.0-mgs.8` remains compatible with `mgs.7`, so existing baselines are retained. Atomic rolling-generation metadata is created automatically. Existing relative-path Solution IDs are reused through a validated alias when the recorded repository root matches. Identical clones in different folders are deliberately isolated into separate indexes.

## STDIO fallback

For clients without Streamable HTTP support:

```toml
[mcp_servers.codemap]
command = 'C:\Tools\MGsCodeMapMCP\MGsCodeMap.Mcp.exe'
args = ['--config', 'C:\Tools\MGsCodeMapMCP\codemap.json']
```

This executable is only a proxy. Start the daemon first, or add `--start-daemon` to the proxy arguments.

## Windows service considerations

A registered Windows service often runs under a service identity with different user environment variables, Git credentials, NuGet sources, certificates, network-drive mappings, temporary directories, and MSBuild discovery. This release therefore supplies the logon-task workflow as the supported default and does not install a service automatically. A true Windows service may be added later as an optional mode after its identity, credential, and permission behavior is separately implemented and tested.

If a managed deployment wraps the daemon as a service, use a deliberately selected identity, absolute paths, explicit permissions, and the same health and graceful-shutdown checks used by the supplied scripts. Validate repository and package access before enabling automatic indexing.

## Troubleshooting

### Port already in use

Change `server.port` in `codemap.json`, restart the daemon, and update the Codex URL. A clear bind error is written to stderr and the log.

### Exit code 17

Another daemon owns the same canonical data directory. Run `status-daemon.ps1` and use the endpoint shown in `data/daemon.lock`. Do not delete the lock file while that PID is alive.

### Daemon starts but indexing cannot load a solution

Run the daemon interactively with `--console`, verify the selected SDK/MSBuild installation, and confirm that the signed-in user can restore and build the solution.

### Task is Ready instead of Running

Check `Get-ScheduledTaskInfo`, `logs/taskhost.log`, and the current daemon log. A non-zero child exit is returned by the Task Host and triggers the configured restart attempts. Common causes are a malformed configuration, a missing executable, an occupied port, or a second owner of the same data directory.

### No console window is visible

This is expected for the installed task. `MGsCodeMap.TaskHost.exe` is a Windows GUI-subsystem entry point and creates the daemon with no console window. Use the explicit `--console` command only when interactive diagnostics are needed.

### High memory during initial indexing

Check `/health`, keep the default indexing limits, and wait for reader idle eviction plus batch memory reclamation. `openWorkspaces`/`logicalWorkspaces` are logical overlay registrations, not loaded `MSBuildWorkspace` or Roslyn Solution instances. `openBaselineReaders`, `openOverlayReaders`, and `roslynIncrementalCacheLoaded` show the actual in-process caches.

### Stop timeout

Check whether `indexing.publishing` is true. Wait for publication and retry. Use `-ForceFallback` only after the health check reports no active publication; WAL recovery protects the last valid records after an unclean exit.
