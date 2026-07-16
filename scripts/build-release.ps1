param([string]$Version = "2.8.0-mgs.6")

$ErrorActionPreference = "Stop"
dotnet pack src/MGsCodeMap.Mcp/MGsCodeMap.Mcp.csproj -c Release "-p:Version=$Version" -o dist/nupkg
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($rid in @("win-x64", "linux-x64", "osx-arm64")) {
    $target = "dist/$rid"
    $proxyTemp = "dist/proxy-$rid"
    dotnet publish src/CodeMap.Daemon/MGsCodeMap.Daemon.csproj -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
        "-p:Version=$Version" -o $target
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet publish src/MGsCodeMap.Mcp/MGsCodeMap.Mcp.csproj -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
        "-p:Version=$Version" -o $proxyTemp
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $proxyName = if ($rid.StartsWith("win")) { "MGsCodeMap.Mcp.exe" } else { "MGsCodeMap.Mcp" }
    Copy-Item -LiteralPath (Join-Path $proxyTemp $proxyName) -Destination $target -Force
    if ($rid.StartsWith("win")) {
        $taskHostTemp = "dist/taskhost-$rid"
        dotnet publish src/MGsCodeMap.TaskHost/MGsCodeMap.TaskHost.csproj -c Release -r $rid --self-contained `
            -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true `
            "-p:Version=$Version" -o $taskHostTemp
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Copy-Item -LiteralPath (Join-Path $taskHostTemp "MGsCodeMap.TaskHost.exe") -Destination $target -Force
    }
}
