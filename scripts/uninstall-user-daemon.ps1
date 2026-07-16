param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon",
    [switch]$ForceFallback
)

& (Join-Path $PSScriptRoot "stop-daemon.ps1") -ConfigPath $ConfigPath -TaskName $TaskName -ForceFallback:$ForceFallback
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $task) {
    if ([string]$task.State -eq "Running") { Stop-ScheduledTask -TaskName $TaskName }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
Write-Host "User logon task removed. Configuration, data, and logs were preserved."
