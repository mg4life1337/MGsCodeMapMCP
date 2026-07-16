param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon",
    [int]$TimeoutSeconds = 30,
    [switch]$ForceFallback
)

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$task = Get-MGsScheduledTask -TaskName $TaskName
$health = Get-MGsDaemonHealth -Url $settings.HealthUrl
if ($null -eq $health) {
    if ($null -ne $task -and [string]$task.State -eq "Running") {
        $health = Wait-MGsDaemonHealth -Url $settings.HealthUrl -TimeoutSeconds ([Math]::Min(5, $TimeoutSeconds))
        if ($null -eq $health) {
            if (-not $ForceFallback) {
                throw "Scheduled task '$TaskName' is running, but health is unavailable. Refusing an unverified force stop."
            }
            Disable-ScheduledTask -TaskName $TaskName | Out-Null
            Stop-ScheduledTask -TaskName $TaskName
            Write-Warning "Stopped task '$TaskName' because health remained unavailable and -ForceFallback was specified."
            return
        }
    }
}
if ($null -eq $health) {
    Write-Host "MGsCodeMap daemon is not running."
    if ($null -ne $task -and [string]$task.State -ne "Disabled") {
        Disable-ScheduledTask -TaskName $TaskName | Out-Null
        Write-Host "Scheduled task disabled to prevent an automatic watchdog start."
    }
    return
}
$pidToStop = [int]$health.processId

if ($null -ne $task) {
    Disable-ScheduledTask -TaskName $TaskName | Out-Null
}

try { Invoke-WebRequest -Uri $settings.ShutdownUrl -Method Post -UseBasicParsing | Out-Null }
catch { Write-Warning "Graceful shutdown request failed: $($_.Exception.Message)" }

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if ($null -eq (Get-Process -Id $pidToStop -ErrorAction SilentlyContinue)) {
        if ($null -ne $task) {
            $task = Wait-MGsScheduledTaskState -TaskName $TaskName -State "Disabled" -TimeoutSeconds 10
            if ($null -ne $task -and [string]$task.State -eq "Running") {
                Stop-ScheduledTask -TaskName $TaskName
                Write-Warning "The daemon exited cleanly, but its task host did not; the empty task instance was stopped."
            }
        }
        Write-Host "MGsCodeMap daemon stopped. PID=$pidToStop"
        return
    }
    Start-Sleep -Milliseconds 250
}

if (-not $ForceFallback) {
    throw "Daemon did not stop within $TimeoutSeconds seconds. Re-run with -ForceFallback for a controlled force stop."
}

$latest = Get-MGsDaemonHealth -Url $settings.HealthUrl
$publishing = if ($null -ne $latest) { [bool]$latest.indexing.publishing } else { [bool]$health.indexing.publishing }
if ($publishing) {
    throw "Force stop refused because an index is currently publishing. Wait and retry."
}
if ($null -ne $task -and [string]$task.State -eq "Running") {
    Stop-ScheduledTask -TaskName $TaskName
    Start-Sleep -Milliseconds 500
}
if ($null -ne (Get-Process -Id $pidToStop -ErrorAction SilentlyContinue)) {
    Stop-Process -Id $pidToStop -Force
}
Write-Warning "MGsCodeMap daemon was force-stopped after the graceful timeout. PID=$pidToStop"
