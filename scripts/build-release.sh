#!/usr/bin/env bash
set -euo pipefail

VERSION=${1:-2.8.0-mgs.4}
echo "Building CodeMap v${VERSION}"

# Pack as .NET global tool
echo "=== Packing .NET global tool ==="
dotnet pack src/CodeMap.Daemon/MGsCodeMap.Mcp.csproj -c Release -p:Version=${VERSION} -o dist/nupkg

# Self-contained binaries (no trimming — Roslyn uses runtime reflection)
for RID in win-x64 linux-x64 osx-arm64; do
  echo "=== Publishing ${RID} ==="
  dotnet publish src/CodeMap.Daemon/MGsCodeMap.Mcp.csproj -c Release -r ${RID} \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:Version=${VERSION} \
    -o dist/${RID}
done

echo ""
echo "=== Build complete ==="
echo "Tool package:  dist/nupkg/MGsCodeMapMCP.${VERSION}.nupkg"
echo "Windows x64:   dist/win-x64/MGsCodeMap.Mcp.exe"
echo "Linux x64:     dist/linux-x64/MGsCodeMap.Mcp"
echo "macOS ARM64:   dist/osx-arm64/MGsCodeMap.Mcp"
