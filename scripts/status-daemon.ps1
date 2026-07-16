param([string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"))

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$health = Get-MGsDaemonHealth -Url $settings.HealthUrl
if ($null -eq $health) {
    throw "MGsCodeMap daemon is not reachable at $($settings.HealthUrl)."
}

$workingSetMb = [Math]::Round([double]$health.memory.workingSetBytes / 1MB, 1)
$privateMb = [Math]::Round([double]$health.memory.privateBytes / 1MB, 1)
Write-Host "MGsCodeMap daemon: $($health.mode)"
Write-Host "Version: $($health.version)"
Write-Host "PID: $($health.processId)"
Write-Host "Endpoint: $($health.endpoint)"
Write-Host "Sessions: $($health.activeSessions)"
Write-Host "Solutions: $($health.loadedSolutions)"
Write-Host "Memory: WorkingSet=${workingSetMb}MB Private=${privateMb}MB"
Write-Host "Index publishing: $($health.indexing.publishing)"
