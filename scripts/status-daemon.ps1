param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon"
)

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$task = Get-MGsScheduledTask -TaskName $TaskName
if ($null -eq $task) {
    Write-Host "Scheduled task: not installed ($TaskName)"
}
else {
    $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName
    Write-Host "Scheduled task: $TaskName"
    Write-Host "Task state: $($task.State)"
    Write-Host "Task last result: $($taskInfo.LastTaskResult)"
    Write-Host "Task last run: $($taskInfo.LastRunTime)"
}
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
Write-Host "Solutions: observed=$($health.repositorySupervisor.observedSolutions) loaded=$($health.loadedSolutions)"
Write-Host "Memory: WorkingSet=${workingSetMb}MB Private=${privateMb}MB"
Write-Host "Index publishing: $($health.indexing.publishing)"
