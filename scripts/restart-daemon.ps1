param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon",
    [int]$TimeoutSeconds = 30,
    [switch]$ForceFallback
)

& (Join-Path $PSScriptRoot "stop-daemon.ps1") -ConfigPath $ConfigPath -TaskName $TaskName -TimeoutSeconds $TimeoutSeconds -ForceFallback:$ForceFallback
& (Join-Path $PSScriptRoot "start-daemon.ps1") -ConfigPath $ConfigPath -TaskName $TaskName -TimeoutSeconds $TimeoutSeconds
