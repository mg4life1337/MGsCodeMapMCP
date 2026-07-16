param(
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot "..\artifacts\release\MGsCodeMapMCP"),
    [string]$TestRoot = (Join-Path $PSScriptRoot "..\artifacts\acceptance-three-client")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$testRoot = [IO.Path]::GetFullPath($TestRoot)
if (-not $testRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Test root must remain inside the repository artifacts directory."
}
if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$daemonExe = Join-Path $release "MGsCodeMap.Daemon.exe"
$proxyExe = Join-Path $release "MGsCodeMap.Mcp.exe"
foreach ($file in @($daemonExe, $proxyExe)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Required executable not found: $file" }
}

$isolatedRepo = Join-Path $testRoot "repository-copy"
Copy-Item -LiteralPath (Join-Path $repoRoot "testdata\SampleSolution") -Destination $isolatedRepo -Recurse
git -C $isolatedRepo init --quiet
git -C $isolatedRepo config user.email "acceptance@example.invalid"
git -C $isolatedRepo config user.name "Acceptance Test"
git -C $isolatedRepo add .
git -C $isolatedRepo commit --quiet -m "isolated acceptance baseline"
if ($LASTEXITCODE -ne 0) { throw "Could not create isolated Git repository." }

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$configPath = Join-Path $testRoot "codemap.json"
$config = [ordered]@{
    dataDirectory = ".\data"
    logDirectory = ".\logs"
    logLevel = "Information"
    server = [ordered]@{
        transport = "streamableHttp"; host = "127.0.0.1"; port = $port
        mcpPath = "/mcp"; healthPath = "/health"; allowRemote = $false
        singleInstance = $true; shutdownTimeoutSeconds = 30
    }
    indexingResources = [ordered]@{
        maxConcurrentIndexes = 1; maxParallelProjects = 2
        incrementalSolutionCacheSize = 1; incrementalSolutionCacheIdleMinutes = 5
        memoryTelemetry = $true
    }
}
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configPath -Encoding UTF8
$healthUrl = "http://127.0.0.1:$port/health"
$mcpUrl = "http://127.0.0.1:$port/mcp"
$shutdownUrl = "http://127.0.0.1:$port/shutdown"
$solutionPath = Join-Path $isolatedRepo "SampleSolution.sln"

function Start-TestDaemon {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $daemonExe
    $info.Arguments = '--config "' + $configPath + '" --console'
    $info.WorkingDirectory = $release
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    return [Diagnostics.Process]::Start($info)
}

function Wait-Health {
    param([int]$Seconds = 30)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try { return Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 } catch { Start-Sleep -Milliseconds 200 }
    }
    throw "Daemon health timeout."
}

$script:requestId = 0
function Invoke-Mcp {
    param([string]$Method, [hashtable]$Parameters, [string]$SessionId)
    $script:requestId++
    $body = @{ jsonrpc = "2.0"; id = $script:requestId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 12 -Compress
    $headers = @{ Accept = "application/json" }
    if ($SessionId) { $headers["Mcp-Session-Id"] = $SessionId }
    $response = Invoke-WebRequest -Uri $mcpUrl -Method Post -Headers $headers -ContentType "application/json" -Body $body -UseBasicParsing
    [pscustomobject]@{
        Json = ($response.Content | ConvertFrom-Json)
        SessionId = [string]$response.Headers["Mcp-Session-Id"]
    }
}

function Invoke-Tool {
    param([string]$Name, [hashtable]$Arguments, [string]$SessionId)
    $call = Invoke-Mcp -Method "tools/call" -Parameters @{ name = $Name; arguments = $Arguments } -SessionId $SessionId
    if ([bool]$call.Json.result.isError) { throw "$Name failed: $($call.Json.result.content[0].text)" }
    return $call
}

function New-AsyncMcpRequest {
    param([string]$Name, [hashtable]$Arguments, [string]$SessionId, [int]$Id)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $mcpUrl)
    $request.Headers.Add("Mcp-Session-Id", $SessionId)
    $body = @{ jsonrpc = "2.0"; id = $Id; method = "tools/call"; params = @{ name = $Name; arguments = $Arguments } } | ConvertTo-Json -Depth 12 -Compress
    $request.Content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, "application/json")
    return $request
}

$daemon = Start-TestDaemon
$proxyProcesses = @()
try {
    $initialHealth = Wait-Health
    $sessions = @()
    foreach ($n in 1..3) {
        $init = Invoke-Mcp -Method "initialize" -Parameters @{ protocolVersion = "2025-03-26"; clientInfo = @{ name = "acceptance-$n"; version = "1" } } -SessionId ""
        $sessions += $init.SessionId
    }
    if (($sessions | Select-Object -Unique).Count -ne 3) { throw "MCP sessions are not unique." }

    $index = Invoke-Tool -Name "index.ensure_baseline" -Arguments @{ repo_path = $isolatedRepo; solution_path = $solutionPath } -SessionId $sessions[0]
    $workspace = Invoke-Tool -Name "workspace.create" -Arguments @{ repo_path = $isolatedRepo; solution_path = $solutionPath; workspace_id = "acceptance-workspace" } -SessionId $sessions[0]

    $changedFile = Join-Path $isolatedRepo "SampleApp\Services\OrderService.cs"
    Add-Content -LiteralPath $changedFile -Value "`n// isolated acceptance change"

    $http = [System.Net.Http.HttpClient]::new()
    try {
        $requests = @(
            (New-AsyncMcpRequest -Name "index.refresh_overlay" -Arguments @{ repo_path = $isolatedRepo; solution_path = $solutionPath; workspace_id = "acceptance-workspace" } -SessionId $sessions[0] -Id 101),
            (New-AsyncMcpRequest -Name "symbols.search" -Arguments @{ repo_path = $isolatedRepo; solution_path = $solutionPath; query = "OrderService" } -SessionId $sessions[1] -Id 102),
            (New-AsyncMcpRequest -Name "symbols.search" -Arguments @{ repo_path = $isolatedRepo; solution_path = $solutionPath; query = "OrderService" } -SessionId $sessions[2] -Id 103),
            (New-AsyncMcpRequest -Name "repo.status" -Arguments @{ repo_path = $isolatedRepo; solution_path = $solutionPath } -SessionId $sessions[1] -Id 104)
        )
        $tasks = @($requests | ForEach-Object { $http.SendAsync($_) })
        [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$tasks)
        foreach ($task in $tasks) {
            $payload = $task.Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json
            if ($null -ne $payload.error -or [bool]$payload.result.isError) { throw "Concurrent MCP request failed." }
        }
    }
    finally { $http.Dispose(); foreach ($request in $requests) { $request.Dispose() } }

    foreach ($n in 1..3) {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $proxyExe
        $info.Arguments = '--config "' + $configPath + '"'
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $proxy = [Diagnostics.Process]::Start($info)
        $proxy.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}')
        $proxy.StandardInput.Flush()
        $line = $proxy.StandardOutput.ReadLine()
        if (-not $line.Contains('"serverInfo"')) { throw "Proxy initialize failed: $line" }
        $proxy.Refresh()
        if ($proxy.WorkingSet64 -ge 100MB) { throw "Proxy idle memory exceeded 100 MB." }
        $proxyProcesses += $proxy
    }

    foreach ($proxy in $proxyProcesses) {
        $proxy.StandardInput.Close()
        if (-not $proxy.WaitForExit(10000)) { throw "Proxy did not exit after stdin closed." }
        if ($proxy.ExitCode -ne 0) { throw "Proxy exited with $($proxy.ExitCode)." }
    }
    if ($daemon.HasExited) { throw "Daemon ended when proxies disconnected." }

    $after = Invoke-RestMethod -Uri $healthUrl
    $daemon.Refresh()
    $logText = (Get-ChildItem (Join-Path $testRoot "logs") -Filter "*.log" | Get-Content -Raw) -join "`n"
    if ($logText -match "overlay\.wal.*used by another process|overlay\.wal.*access") {
        throw "A WAL sharing error was logged."
    }
    $daemonCount = @(Get-CimInstance Win32_Process -Filter "Name='MGsCodeMap.Daemon.exe'" | Where-Object { $_.ExecutablePath -eq $daemonExe }).Count
    if ($daemonCount -ne 1) { throw "Expected one daemon process for the test release, found $daemonCount." }

    Invoke-WebRequest -Uri $shutdownUrl -Method Post -UseBasicParsing | Out-Null
    if (-not $daemon.WaitForExit(30000)) { throw "Daemon did not stop gracefully." }

    $daemon = Start-TestDaemon
    $restartHealth = Wait-Health
    $restartSession = (Invoke-Mcp -Method "initialize" -Parameters @{ protocolVersion = "2025-03-26" } -SessionId "").SessionId
    $status = Invoke-Tool -Name "repo.status" -Arguments @{ repo_path = $isolatedRepo; solution_path = $solutionPath } -SessionId $restartSession
    $statusText = [string]$status.Json.result.content[0].text
    if (-not $statusText.Contains('"baseline_index_exists":true')) { throw "Baseline was not retained across restart." }

    $result = [ordered]@{
        version = $restartHealth.version
        daemonPid = $restartHealth.processId
        directSessions = 3
        proxyProcesses = 3
        daemonProcessCount = $daemonCount
        daemonWorkingSetBytesAfterIndex = $after.memory.workingSetBytes
        daemonPrivateBytesAfterIndex = $after.memory.privateBytes
        maxProxyWorkingSetBytes = ($proxyProcesses | ForEach-Object { $_.WorkingSet64 } | Measure-Object -Maximum).Maximum
        activeFullIndexesAfterRun = $after.indexing.activeFullIndexes
        walSharingErrors = 0
        baselineRetainedAfterRestart = $true
        dataDirectory = $restartHealth.dataDirectory
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot "result.json") -Encoding UTF8
    $result | ConvertTo-Json -Depth 5
}
finally {
    foreach ($proxy in $proxyProcesses) {
        if (-not $proxy.HasExited) { $proxy.Kill() }
        $proxy.Dispose()
    }
    if ($null -ne $daemon -and -not $daemon.HasExited) {
        try { Invoke-WebRequest -Uri $shutdownUrl -Method Post -UseBasicParsing | Out-Null; $daemon.WaitForExit(10000) | Out-Null } catch { }
        if (-not $daemon.HasExited) { $daemon.Kill() }
    }
    if ($null -ne $daemon) { $daemon.Dispose() }
}
