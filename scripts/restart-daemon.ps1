param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [int]$TimeoutSeconds = 30,
    [switch]$ForceFallback
)

& (Join-Path $PSScriptRoot "stop-daemon.ps1") -ConfigPath $ConfigPath -TimeoutSeconds $TimeoutSeconds -ForceFallback:$ForceFallback
& (Join-Path $PSScriptRoot "start-daemon.ps1") -ConfigPath $ConfigPath -TimeoutSeconds $TimeoutSeconds
