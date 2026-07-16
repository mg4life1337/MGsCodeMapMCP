param(
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot "..\artifacts\release\MGsCodeMapMCP"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon Acceptance",
    [int]$RestartTimeoutSeconds = 100
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$testRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "task host acceptance"))
$install = Join-Path $testRoot "MGs CodeMap MCP"
$repository = Join-Path $testRoot "Sample Repository"
$config = Join-Path $install "codemap.json"
$data = Join-Path $install "data"
$logs = Join-Path $install "logs"
$resultPath = Join-Path $testRoot "result.json"
$taskHostExe = Join-Path $install "MGsCodeMap.TaskHost.exe"
$daemonExe = Join-Path $install "MGsCodeMap.Daemon.exe"
$installScript = Join-Path $install "scripts\install-user-daemon.ps1"
$uninstallScript = Join-Path $install "scripts\uninstall-user-daemon.ps1"
$startScript = Join-Path $install "scripts\start-daemon.ps1"
$stopScript = Join-Path $install "scripts\stop-daemon.ps1"
$restartScript = Join-Path $install "scripts\restart-daemon.ps1"
$release = [IO.Path]::GetFullPath($ReleaseDirectory)

if (-not $testRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe acceptance-test directory."
}
if (-not (Test-Path -LiteralPath (Join-Path $release "MGsCodeMap.TaskHost.exe"))) {
    throw "Release directory does not contain MGsCodeMap.TaskHost.exe: $release"
}
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    throw "Acceptance task already exists: $TaskName"
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Get-Health {
    param([string]$Url)
    try { return Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 2 }
    catch { return $null }
}

function Wait-Health {
    param([string]$Url, [int]$TimeoutSeconds, [int]$DifferentFromPid = 0)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $health = Get-Health -Url $Url
        if ($null -ne $health -and ($DifferentFromPid -eq 0 -or [int]$health.processId -ne $DifferentFromPid)) {
            return $health
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Get-ProcessForExecutable {
    param([string]$Name, [string]$Executable)
    return @(Get-CimInstance Win32_Process -Filter "Name='$Name'" |
        Where-Object { $_.ExecutablePath -eq $Executable })
}

function Get-PeSubsystem {
    param([string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    return [BitConverter]::ToUInt16($bytes, $peOffset + 24 + 68)
}

$port = Get-FreePort
$healthUrl = "http://127.0.0.1:$port/health"
$completed = $false
try {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Copy-Item -LiteralPath $release -Destination $install -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "testdata\SampleSolution") -Destination $repository -Recurse -Force
    Get-ChildItem -LiteralPath $repository -Recurse -Directory -Force |
        Where-Object { $_.Name -in @("bin", "obj", ".git") } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force

    $configuration = [ordered]@{
        dataDirectory = ".\data"
        logDirectory = ".\logs"
        logLevel = "Information"
        server = [ordered]@{
            host = "127.0.0.1"
            port = $port
            mcpPath = "/mcp"
            healthPath = "/health"
            singleInstance = $true
        }
        repositories = @(
            [ordered]@{
                root = $repository
                solutions = @("SampleSolution.sln")
                discoverSolutions = $false
                autoIndex = $false
                watchGitHead = $false
            }
        )
    }
    $configuration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config -Encoding utf8
    New-Item -ItemType Directory -Path $data, $logs -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $data "preserve.marker") -Value "data"
    Set-Content -LiteralPath (Join-Path $logs "preserve.marker") -Value "logs"

    if ((Get-PeSubsystem -Path $taskHostExe) -ne 2) {
        throw "Task Host PE subsystem is not Windows GUI."
    }

    & $installScript -ConfigPath $config -TaskName $TaskName -TimeoutSeconds 30
    $health = Wait-Health -Url $healthUrl -TimeoutSeconds 30
    if ($null -eq $health) { throw "Health did not become available after installation." }

    $solutionDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $solutionDeadline -and [int]$health.repositorySupervisor.observedSolutions -ne 1) {
        Start-Sleep -Milliseconds 250
        $health = Get-Health -Url $healthUrl
    }
    if ([int]$health.repositorySupervisor.observedSolutions -ne 1) {
        throw "Expected one configured Solution; observed $($health.repositorySupervisor.observedSolutions)."
    }

    $task = Get-ScheduledTask -TaskName $TaskName
    $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName
    if ([string]$task.State -ne "Running") { throw "Task is not Running after installer exit: $($task.State)" }
    if ([string]$task.Principal.LogonType -ne "Interactive") { throw "Task does not use Interactive logon." }
    if ([string]$task.Principal.RunLevel -ne "Limited") { throw "Task is not using Limited run level." }
    $taskXml = Export-ScheduledTask -TaskName $TaskName
    if ($taskXml -match '<Password>') { throw "Task definition contains a stored password." }
    if ($taskXml -notmatch '<LogonType>InteractiveToken</LogonType>') { throw "Task XML does not use InteractiveToken." }
    if ($task.Actions.Execute -ne $taskHostExe) { throw "Task action does not execute the Task Host." }
    if ($task.Actions.Execute -match 'powershell|pwsh|cmd') { throw "Task action uses a shell wrapper." }
    if (-not [bool]$task.Settings.StartWhenAvailable) { throw "Task is not configured to start when available." }
    if ([bool]$task.Settings.DisallowStartIfOnBatteries) { throw "Task is blocked on battery power." }
    if ([bool]$task.Settings.StopIfGoingOnBatteries) { throw "Task stops on battery power." }
    if ([int]$task.Settings.RestartCount -lt 1) { throw "Task has no restart-on-failure policy." }
    if (@($task.Triggers).Count -lt 2) { throw "Task does not contain both logon and watchdog triggers." }

    $taskHosts = Get-ProcessForExecutable -Name "MGsCodeMap.TaskHost.exe" -Executable $taskHostExe
    $daemons = Get-ProcessForExecutable -Name "MGsCodeMap.Daemon.exe" -Executable $daemonExe
    if ($taskHosts.Count -ne 1 -or $daemons.Count -ne 1) {
        throw "Expected one Task Host and one daemon; found $($taskHosts.Count) and $($daemons.Count)."
    }
    if ([int]$taskHosts[0].ParentProcessId -eq $PID) { throw "Task Host is still attached to the installer shell." }
    if ((Get-Process -Id $taskHosts[0].ProcessId).MainWindowHandle -ne 0) { throw "Task Host owns a visible window." }
    if ((Get-Process -Id $daemons[0].ProcessId).MainWindowHandle -ne 0) { throw "Daemon owns a visible window." }

    $beforeReinstallPid = [int]$health.processId
    & $installScript -ConfigPath $config -TaskName $TaskName -TimeoutSeconds 30
    $health = Wait-Health -Url $healthUrl -TimeoutSeconds 30 -DifferentFromPid $beforeReinstallPid
    if ($null -eq $health) { throw "Repeated installation did not restart the upgraded task." }
    if ([string](Get-ScheduledTask -TaskName $TaskName).State -ne "Running") {
        throw "Task is not Running after repeated installation."
    }
    $taskHosts = Get-ProcessForExecutable -Name "MGsCodeMap.TaskHost.exe" -Executable $taskHostExe
    $daemons = Get-ProcessForExecutable -Name "MGsCodeMap.Daemon.exe" -Executable $daemonExe
    if ($taskHosts.Count -ne 1 -or $daemons.Count -ne 1) {
        throw "Repeated installation left an unexpected process count."
    }

    $firstDaemonPid = [int]$health.processId
    $firstTaskHostPid = [int]$taskHosts[0].ProcessId
    Stop-Process -Id $firstDaemonPid -Force
    $afterCrash = Wait-Health -Url $healthUrl -TimeoutSeconds $RestartTimeoutSeconds -DifferentFromPid $firstDaemonPid
    if ($null -eq $afterCrash) { throw "Task Scheduler did not restart the daemon after the controlled crash." }
    $taskAfterCrash = Get-ScheduledTask -TaskName $TaskName
    if ([string]$taskAfterCrash.State -ne "Running") { throw "Task is not Running after crash restart." }
    $taskHostsAfterCrash = Get-ProcessForExecutable -Name "MGsCodeMap.TaskHost.exe" -Executable $taskHostExe
    if ($taskHostsAfterCrash.Count -ne 1 -or [int]$taskHostsAfterCrash[0].ProcessId -eq $firstTaskHostPid) {
        throw "Task Host process was not restarted after daemon failure."
    }
    $taskHostLog = Get-Content -LiteralPath (Join-Path $logs "taskhost.log") -Raw
    if ($taskHostLog -notmatch 'daemon_exited exit_code=-?\d+') { throw "Task Host did not record the daemon exit code." }

    $beforeRestartPid = [int]$afterCrash.processId
    & $restartScript -ConfigPath $config -TaskName $TaskName -TimeoutSeconds 30
    $afterRestart = Wait-Health -Url $healthUrl -TimeoutSeconds 30 -DifferentFromPid $beforeRestartPid
    if ($null -eq $afterRestart) { throw "restart-daemon.ps1 did not start a new daemon process." }
    if ([string](Get-ScheduledTask -TaskName $TaskName).State -ne "Running") { throw "Task is not Running after restart." }

    & $stopScript -ConfigPath $config -TaskName $TaskName -TimeoutSeconds 30
    if ($null -ne (Get-Health -Url $healthUrl)) { throw "Health is still reachable after stop." }
    if ([string](Get-ScheduledTask -TaskName $TaskName).State -ne "Disabled") { throw "Task is not Disabled after stop." }

    & $startScript -ConfigPath $config -TaskName $TaskName -TimeoutSeconds 30
    $afterStart = Wait-Health -Url $healthUrl -TimeoutSeconds 30
    if ($null -eq $afterStart) { throw "start-daemon.ps1 did not restore health." }
    & $uninstallScript -ConfigPath $config -TaskName $TaskName

    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { throw "Task still exists after uninstall." }
    if ($null -ne (Get-Health -Url $healthUrl)) { throw "Daemon is still running after uninstall." }
    foreach ($path in @($config, (Join-Path $data "preserve.marker"), (Join-Path $logs "preserve.marker"))) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Preserved test data is missing: $path" }
    }
    $unexpected = @(Get-ChildItem -LiteralPath $repository -Recurse -Directory -Force |
        Where-Object { $_.Name -in @(".codex", ".codemap") })
    if ($unexpected.Count -ne 0) { throw "Unexpected metadata directory was created in the test repository." }

    $result = [ordered]@{
        version = [string]$afterStart.version
        taskName = $TaskName
        taskAction = "MGsCodeMap.TaskHost.exe"
        taskStateDuringDaemon = "Running"
        taskStateAfterStop = "Disabled"
        interactiveLogon = $true
        limitedRunLevel = $true
        storedPassword = $false
        windowlessTaskHost = $true
        windowlessDaemon = $true
        observedSolutions = 1
        installerDetached = $true
        repeatedInstallSucceeded = $true
        crashRestarted = $true
        crashExitCodeForwarded = $true
        crashOldDaemonPid = $firstDaemonPid
        crashNewDaemonPid = [int]$afterCrash.processId
        restartOldDaemonPid = $beforeRestartPid
        restartNewDaemonPid = [int]$afterRestart.processId
        taskLastResultDuringRun = [int64]$taskInfo.LastTaskResult
        configuredRestartCount = [int]$task.Settings.RestartCount
        watchdogTriggerInstalled = $true
        dataPreserved = $true
        logsPreserved = $true
        metadataDirectoriesCreated = 0
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
    $result | ConvertTo-Json -Depth 5
    $completed = $true
}
finally {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        try { & $uninstallScript -ConfigPath $config -TaskName $TaskName -ForceFallback } catch {
            try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { }
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
    if (-not $completed) {
        foreach ($process in @(Get-ProcessForExecutable -Name "MGsCodeMap.Daemon.exe" -Executable $daemonExe)) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
        foreach ($process in @(Get-ProcessForExecutable -Name "MGsCodeMap.TaskHost.exe" -Executable $taskHostExe)) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}
