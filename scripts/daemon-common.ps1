$ErrorActionPreference = "Stop"
$script:MGsDefaultTaskName = "MGsCodeMapMCP User Daemon"

function Resolve-MGsDaemonSettings {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    $resolvedConfig = [System.IO.Path]::GetFullPath($ConfigPath)
    $installDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $daemonExe = [System.IO.Path]::GetFullPath((Join-Path $installDirectory "MGsCodeMap.Daemon.exe"))
    $taskHostExe = [System.IO.Path]::GetFullPath((Join-Path $installDirectory "MGsCodeMap.TaskHost.exe"))
    if (-not (Test-Path -LiteralPath $daemonExe)) { throw "Daemon executable not found: $daemonExe" }
    if (-not (Test-Path -LiteralPath $taskHostExe)) { throw "Task host executable not found: $taskHostExe" }
    if (-not (Test-Path -LiteralPath $resolvedConfig)) { throw "Configuration file not found: $resolvedConfig" }

    $hostName = "127.0.0.1"
    $port = 5137
    $healthPath = "/health"
    if (Test-Path -LiteralPath $resolvedConfig) {
        $config = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json
        if ($null -ne $config.server) {
            if ($config.server.host) { $hostName = [string]$config.server.host }
            if ($config.server.port) { $port = [int]$config.server.port }
            if ($config.server.healthPath) { $healthPath = [string]$config.server.healthPath }
        }
    }
    if (-not $healthPath.StartsWith("/")) { $healthPath = "/$healthPath" }

    [pscustomobject]@{
        ConfigPath = $resolvedConfig
        InstallDirectory = $installDirectory
        DaemonExecutable = $daemonExe
        TaskHostExecutable = $taskHostExe
        HealthUrl = "http://${hostName}:${port}${healthPath}"
        ShutdownUrl = "http://${hostName}:${port}/shutdown"
    }
}

function ConvertTo-MGsQuotedArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value.Contains('"')) { throw 'Windows paths containing a quotation mark are not supported.' }
    return '"' + $Value + '"'
}

function Get-MGsScheduledTask {
    param([string]$TaskName = $script:MGsDefaultTaskName)
    return Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}

function Wait-MGsScheduledTaskState {
    param(
        [Parameter(Mandatory = $true)][string]$TaskName,
        [Parameter(Mandatory = $true)][string]$State,
        [int]$TimeoutSeconds = 10
    )
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $task = Get-MGsScheduledTask -TaskName $TaskName
        if ($null -eq $task -or [string]$task.State -eq $State) { return $task }
        Start-Sleep -Milliseconds 250
    }
    return Get-MGsScheduledTask -TaskName $TaskName
}

function Get-MGsDaemonHealth {
    param([Parameter(Mandatory = $true)][string]$Url, [int]$TimeoutSeconds = 2)
    try {
        return Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec $TimeoutSeconds
    }
    catch { return $null }
}

function Wait-MGsDaemonHealth {
    param([Parameter(Mandatory = $true)][string]$Url, [int]$TimeoutSeconds = 30)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $health = Get-MGsDaemonHealth -Url $Url
        if ($null -ne $health) { return $health }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
