param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon",
    [int]$TimeoutSeconds = 30
)

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$current = Get-MGsDaemonHealth -Url $settings.HealthUrl
if ($null -ne $current) {
    & (Join-Path $PSScriptRoot "stop-daemon.ps1") -ConfigPath $settings.ConfigPath -TaskName $TaskName -TimeoutSeconds $TimeoutSeconds
}

$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$argument = '--daemon ' + (ConvertTo-MGsQuotedArgument $settings.DaemonExecutable) +
    ' --config ' + (ConvertTo-MGsQuotedArgument $settings.ConfigPath)
$action = New-ScheduledTaskAction -Execute $settings.TaskHostExecutable -Argument $argument -WorkingDirectory $settings.InstallDirectory
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$watchdogTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1)
$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Compatibility Win7
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($logonTrigger, $watchdogTrigger) -Settings $taskSettings -Principal $principal -Force | Out-Null
Write-Host "User logon task installed: $TaskName (Interactive, Limited, no stored password)"
& (Join-Path $PSScriptRoot "start-daemon.ps1") -ConfigPath $settings.ConfigPath -TaskName $TaskName -TimeoutSeconds $TimeoutSeconds
