namespace CodeMap.Integration.Tests.EndToEnd;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;

[Trait("Category", "Integration")]
public sealed class CentralDaemonHttpTests
{
    private static readonly string DaemonDll = Path.Combine(AppContext.BaseDirectory, "MGsCodeMap.Daemon.dll");

    [Fact]
    public async Task ThreeSessions_ShareOneDaemon_AndSecondWriterIsRejected()
    {
        File.Exists(DaemonDll).Should().BeTrue();
        var root = Path.Combine(Path.GetTempPath(), "mgscodemap-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var port = GetFreePort();
        var config = Path.Combine(root, "codemap.json");
        File.WriteAllText(config, $$"""
            {
              "dataDirectory": ".\\data",
              "logDirectory": ".\\logs",
              "server": { "host": "127.0.0.1", "port": {{port}} }
            }
            """);

        using var daemon = Start(DaemonDll, config);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var health = new Uri($"http://127.0.0.1:{port}/health");
        var mcp = new Uri($"http://127.0.0.1:{port}/mcp");
        try
        {
            await WaitForHealthAsync(client, health);

            var initializations = await Task.WhenAll(Enumerable.Range(1, 3)
                .Select(id => PostAsync(client, mcp,
                    JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id,
                        method = "initialize",
                        @params = new { protocolVersion = "2025-03-26" },
                    }))));
            initializations.Should().OnlyContain(result => result.Body.Contains("serverInfo", StringComparison.Ordinal));
            initializations.Select(result => result.SessionId).Should().OnlyHaveUniqueItems();

            using var healthResponse = await client.GetAsync(health);
            using var healthJson = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            healthJson.RootElement.GetProperty("processId").GetInt32().Should().Be(daemon.Id);
            healthJson.RootElement.GetProperty("activeSessions").GetInt32().Should().Be(3);

            var walDirectory = Path.Combine(root, "data", "wal-guard");
            Directory.CreateDirectory(walDirectory);
            var walPath = Path.Combine(walDirectory, "overlay.wal");
            var walBytes = new byte[] { 7, 11, 13, 17 };
            File.WriteAllBytes(walPath, walBytes);
            var walTimestamp = File.GetLastWriteTimeUtc(walPath);

            using var second = Start(DaemonDll, config);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await second.WaitForExitAsync(timeout.Token);
            second.ExitCode.Should().Be(DaemonInstanceExitCode);
            File.ReadAllBytes(walPath).Should().Equal(walBytes);
            File.GetLastWriteTimeUtc(walPath).Should().Be(walTimestamp);

            var lists = await Task.WhenAll(initializations.Select((session, index) =>
                PostAsync(client, mcp,
                    JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = index + 10,
                        method = "tools/list",
                        @params = new { },
                    }),
                    session.SessionId)));
            lists.Should().OnlyContain(result => result.Body.Contains("symbols.search", StringComparison.Ordinal));
            File.Delete(walPath);
            Directory.EnumerateFiles(Path.Combine(root, "data"), "overlay.wal", SearchOption.AllDirectories).Should().BeEmpty();

            using var shutdown = await client.PostAsync(
                new Uri($"http://127.0.0.1:{port}/shutdown"), new StringContent("", Encoding.UTF8));
            shutdown.StatusCode.Should().Be(HttpStatusCode.Accepted);
            await daemon.WaitForExitAsync(timeout.Token);
            daemon.ExitCode.Should().Be(0);
        }
        finally
        {
            if (!daemon.HasExited) daemon.Kill(entireProcessTree: true);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private const int DaemonInstanceExitCode = 17;

    private static Process Start(string dll, string config)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start daemon test process.");
    }

    private static async Task WaitForHealthAsync(HttpClient client, Uri endpoint)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { if ((await client.GetAsync(endpoint)).IsSuccessStatusCode) return; } catch { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Daemon health endpoint did not become ready.");
    }

    private static async Task<(string Body, string SessionId)> PostAsync(
        HttpClient client, Uri endpoint, string json, string? sessionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var actualSession = response.Headers.GetValues("Mcp-Session-Id").Single();
        return (await response.Content.ReadAsStringAsync(), actualSession);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
