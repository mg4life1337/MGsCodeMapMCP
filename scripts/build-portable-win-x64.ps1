param(
    [string]$Version = "2.8.0-mgs.1"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\release"))
$publishDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "publish-win-x64"))
$releaseDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "MGsCodeMapMCP"))
$zipPath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "MGsCodeMapMCP-win-x64.zip"))

foreach ($path in @($publishDir, $releaseDir, $zipPath)) {
    if (-not $path.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside artifacts/release: $path"
    }
}

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
foreach ($directory in @($publishDir, $releaseDir)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

dotnet publish (Join-Path $repoRoot "src\CodeMap.Daemon\CodeMap.Daemon.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $releaseDir -Recurse -Force

$daemonExe = Join-Path $releaseDir "CodeMap.Daemon.exe"
if (-not (Test-Path -LiteralPath $daemonExe)) {
    throw "Published executable not found: $daemonExe"
}
Copy-Item -LiteralPath $daemonExe -Destination (Join-Path $releaseDir "CodeMap.Mcp.exe") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "codemap.example.json") -Destination $releaseDir
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $releaseDir
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE.MD") -Destination $releaseDir
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -Destination $releaseDir
New-Item -ItemType Directory -Force -Path (Join-Path $releaseDir "data") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $releaseDir "logs") | Out-Null

Compress-Archive -LiteralPath $releaseDir -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Portable release: $releaseDir"
Write-Host "Archive:          $zipPath"
