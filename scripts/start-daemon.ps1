param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon",
    [int]$TimeoutSeconds = 30
)

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$current = Get-MGsDaemonHealth -Url $settings.HealthUrl
if ($null -ne $current) {
    $existingTask = Get-MGsScheduledTask -TaskName $TaskName
    $taskState = if ($null -eq $existingTask) { "not installed" } else { [string]$existingTask.State }
    Write-Host "MGsCodeMap daemon is already running. PID=$($current.processId) Version=$($current.version) Task=$taskState"
    return
}

$task = Get-MGsScheduledTask -TaskName $TaskName
$startedPid = $null
if ($null -ne $task) {
    Enable-ScheduledTask -TaskName $TaskName | Out-Null
    Start-ScheduledTask -TaskName $TaskName
}
else {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $settings.TaskHostExecutable
    $startInfo.WorkingDirectory = $settings.InstallDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add("--daemon")
    $startInfo.ArgumentList.Add($settings.DaemonExecutable)
    $startInfo.ArgumentList.Add("--config")
    $startInfo.ArgumentList.Add($settings.ConfigPath)
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "Failed to start MGsCodeMap task host." }
    $startedPid = $process.Id
}

$health = Wait-MGsDaemonHealth -Url $settings.HealthUrl -TimeoutSeconds $TimeoutSeconds
if ($null -eq $health) {
    $detail = if ($null -ne $task) {
        $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
        "Task=$TaskName State=$((Get-MGsScheduledTask -TaskName $TaskName).State) LastResult=$($info.LastTaskResult)"
    } else { "TaskHostPID=$startedPid" }
    throw "Daemon did not become healthy within $TimeoutSeconds seconds. $detail"
}
$mode = if ($null -ne $task) { "scheduled task '$TaskName'" } else { "windowless task host" }
Write-Host "MGsCodeMap daemon started through $mode. PID=$($health.processId) Version=$($health.version) Endpoint=$($health.endpoint)"
