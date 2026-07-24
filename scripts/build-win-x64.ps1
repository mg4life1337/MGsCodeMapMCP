param([string]$Version = "2.8.0-mgs.7")

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\release"))
$daemonPublish = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "publish-daemon-win-x64"))
$proxyPublish = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "publish-proxy-win-x64"))
$taskHostPublish = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "publish-taskhost-win-x64"))
$releaseDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "MGsCodeMapMCP"))
$zipPath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "MGsCodeMapMCP-win-x64.zip"))

foreach ($path in @($daemonPublish, $proxyPublish, $taskHostPublish, $releaseDir, $zipPath)) {
    if (-not $path.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside artifacts/release: $path"
    }
}

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
foreach ($directory in @($daemonPublish, $proxyPublish, $taskHostPublish, $releaseDir)) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

dotnet publish (Join-Path $repoRoot "src\CodeMap.Daemon\MGsCodeMap.Daemon.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$Version -o $daemonPublish
if ($LASTEXITCODE -ne 0) { throw "Daemon publish failed with exit code $LASTEXITCODE" }

dotnet publish (Join-Path $repoRoot "src\MGsCodeMap.Mcp\MGsCodeMap.Mcp.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $proxyPublish
if ($LASTEXITCODE -ne 0) { throw "Proxy publish failed with exit code $LASTEXITCODE" }

dotnet publish (Join-Path $repoRoot "src\MGsCodeMap.TaskHost\MGsCodeMap.TaskHost.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true `
    -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $taskHostPublish
if ($LASTEXITCODE -ne 0) { throw "Task host publish failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
Copy-Item -Path (Join-Path $daemonPublish "*") -Destination $releaseDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $proxyPublish "MGsCodeMap.Mcp.exe") -Destination $releaseDir -Force
Copy-Item -LiteralPath (Join-Path $taskHostPublish "MGsCodeMap.TaskHost.exe") -Destination $releaseDir -Force
Get-ChildItem -LiteralPath $releaseDir -Recurse -Filter "*.pdb" | Remove-Item -Force

foreach ($required in @("MGsCodeMap.Daemon.exe", "MGsCodeMap.Mcp.exe", "MGsCodeMap.TaskHost.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseDir $required))) {
        throw "Published executable not found: $required"
    }
}
foreach ($oldName in @("CodeMap.Mcp.exe", "CodeMap.Daemon.exe")) {
    if (Test-Path -LiteralPath (Join-Path $releaseDir $oldName)) {
        throw "Old executable must not be included: $oldName"
    }
}

foreach ($file in @("codemap.example.json", "README.md", "LICENSE.MD", "THIRD-PARTY-NOTICES.md")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $releaseDir
}
New-Item -ItemType Directory -Force -Path (Join-Path $releaseDir "docs") | Out-Null
foreach ($doc in @("CENTRAL-DAEMON.MD", "WINDOWS-INSTALLATION.MD", "ROLLING-BRANCH-INDEXING.MD")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$doc") -Destination (Join-Path $releaseDir "docs")
}
New-Item -ItemType Directory -Force -Path (Join-Path $releaseDir "scripts") | Out-Null
foreach ($script in @(
    "daemon-common.ps1", "install-user-daemon.ps1", "uninstall-user-daemon.ps1",
    "start-daemon.ps1", "stop-daemon.ps1", "restart-daemon.ps1", "status-daemon.ps1")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\$script") -Destination (Join-Path $releaseDir "scripts")
}
New-Item -ItemType Directory -Force -Path (Join-Path $releaseDir "data") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $releaseDir "logs") | Out-Null

Compress-Archive -LiteralPath $releaseDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Windows x64 release: $releaseDir"
Write-Host "Archive: $zipPath"
