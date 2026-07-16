param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon",
    [switch]$ForceFallback
)

& (Join-Path $PSScriptRoot "stop-daemon.ps1") -ConfigPath $ConfigPath -ForceFallback:$ForceFallback
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
Write-Host "User logon task removed. Configuration, data, and logs were preserved."
