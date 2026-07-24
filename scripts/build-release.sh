#!/usr/bin/env bash
set -euo pipefail

VERSION=${1:-2.8.0-mgs.7}
dotnet pack src/MGsCodeMap.Mcp/MGsCodeMap.Mcp.csproj -c Release -p:Version="${VERSION}" -o dist/nupkg

for RID in win-x64 linux-x64 osx-arm64; do
  TARGET="dist/${RID}"
  PROXY="dist/proxy-${RID}"
  dotnet publish src/CodeMap.Daemon/MGsCodeMap.Daemon.csproj -c Release -r "${RID}" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false \
    -p:Version="${VERSION}" -o "${TARGET}"
  dotnet publish src/MGsCodeMap.Mcp/MGsCodeMap.Mcp.csproj -c Release -r "${RID}" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false \
    -p:Version="${VERSION}" -o "${PROXY}"
  if [[ "${RID}" == win-* ]]; then
    cp "${PROXY}/MGsCodeMap.Mcp.exe" "${TARGET}/MGsCodeMap.Mcp.exe"
    TASK_HOST="dist/taskhost-${RID}"
    dotnet publish src/MGsCodeMap.TaskHost/MGsCodeMap.TaskHost.csproj -c Release -r "${RID}" --self-contained \
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true \
      -p:Version="${VERSION}" -o "${TASK_HOST}"
    cp "${TASK_HOST}/MGsCodeMap.TaskHost.exe" "${TARGET}/MGsCodeMap.TaskHost.exe"
  else
    cp "${PROXY}/MGsCodeMap.Mcp" "${TARGET}/MGsCodeMap.Mcp"
  fi
done
