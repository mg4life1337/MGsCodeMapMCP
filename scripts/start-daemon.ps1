param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [int]$TimeoutSeconds = 30
)

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$current = Get-MGsDaemonHealth -Url $settings.HealthUrl
if ($null -ne $current) {
    Write-Host "MGsCodeMap daemon is already running. PID=$($current.processId) Version=$($current.version)"
    return
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $settings.Executable
$startInfo.Arguments = '--config "' + $settings.ConfigPath.Replace('"', '\"') + '"'
$startInfo.WorkingDirectory = $settings.InstallDirectory
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::Start($startInfo)
if ($null -eq $process) { throw "Failed to start MGsCodeMap daemon." }

$health = Wait-MGsDaemonHealth -Url $settings.HealthUrl -TimeoutSeconds $TimeoutSeconds
if ($null -eq $health) {
    throw "Daemon did not become healthy within $TimeoutSeconds seconds. Started PID=$($process.Id)."
}
Write-Host "MGsCodeMap daemon started. PID=$($health.processId) Version=$($health.version) Endpoint=$($health.endpoint)"
