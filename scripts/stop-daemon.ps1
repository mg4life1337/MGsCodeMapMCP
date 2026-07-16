param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [int]$TimeoutSeconds = 30,
    [switch]$ForceFallback
)

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$health = Get-MGsDaemonHealth -Url $settings.HealthUrl
if ($null -eq $health) {
    Write-Host "MGsCodeMap daemon is not running."
    return
}
$pidToStop = [int]$health.processId

try { Invoke-WebRequest -Uri $settings.ShutdownUrl -Method Post -UseBasicParsing | Out-Null }
catch { Write-Warning "Graceful shutdown request failed: $($_.Exception.Message)" }

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if ($null -eq (Get-Process -Id $pidToStop -ErrorAction SilentlyContinue)) {
        Write-Host "MGsCodeMap daemon stopped. PID=$pidToStop"
        return
    }
    Start-Sleep -Milliseconds 250
}

if (-not $ForceFallback) {
    throw "Daemon did not stop within $TimeoutSeconds seconds. Re-run with -ForceFallback for a controlled force stop."
}

$latest = Get-MGsDaemonHealth -Url $settings.HealthUrl
if ($null -ne $latest -and [bool]$latest.indexing.publishing) {
    throw "Force stop refused because an index is currently publishing. Wait and retry."
}
Stop-Process -Id $pidToStop -Force
Write-Warning "MGsCodeMap daemon was force-stopped after the graceful timeout. PID=$pidToStop"
