$ErrorActionPreference = "Stop"

function Resolve-MGsDaemonSettings {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    $resolvedConfig = [System.IO.Path]::GetFullPath($ConfigPath)
    $installDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $exe = [System.IO.Path]::GetFullPath((Join-Path $installDirectory "MGsCodeMap.Daemon.exe"))
    if (-not (Test-Path -LiteralPath $exe)) { throw "Daemon executable not found: $exe" }

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
        Executable = $exe
        HealthUrl = "http://${hostName}:${port}${healthPath}"
        ShutdownUrl = "http://${hostName}:${port}/shutdown"
    }
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
