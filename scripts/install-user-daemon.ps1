param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "..\codemap.json"),
    [string]$TaskName = "MGsCodeMapMCP User Daemon"
)

. (Join-Path $PSScriptRoot "daemon-common.ps1")
$settings = Resolve-MGsDaemonSettings -ConfigPath $ConfigPath
$argument = '--config "' + $settings.ConfigPath.Replace('"', '""') + '"'
$action = New-ScheduledTaskAction -Execute $settings.Executable -Argument $argument -WorkingDirectory $settings.InstallDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $taskSettings -Principal $principal -Force | Out-Null
Write-Host "User logon task installed: $TaskName"
& (Join-Path $PSScriptRoot "start-daemon.ps1") -ConfigPath $settings.ConfigPath
