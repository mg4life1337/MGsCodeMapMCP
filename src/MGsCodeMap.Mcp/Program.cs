namespace MGsCodeMap.Mcp;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Program
{
    private const int MaxContentLength = 10 * 1024 * 1024;

    internal static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--version" or "-v"))
        {
            Console.WriteLine($"MGsCodeMapMCP {Version}");
            return 0;
        }

        ProxyOptions options;
        try { options = ProxyOptions.Resolve(args); }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException)
        {
            Console.Error.WriteLine($"MGsCodeMap MCP proxy configuration failed: {ex.Message}");
            return 2;
        }

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!await IsHealthyAsync(client, options.HealthEndpoint).ConfigureAwait(false) &&
            options.StartDaemon)
        {
            TryStartDaemon(options);
            await WaitForHealthAsync(client, options.HealthEndpoint, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }

        if (!await IsHealthyAsync(client, options.HealthEndpoint).ConfigureAwait(false))
        {
            Console.Error.WriteLine(
                $"MGsCodeMap daemon is not reachable at {options.McpEndpoint}. " +
                "Run scripts\\start-daemon.ps1 or install the user daemon.");
            return 4;
        }

        using var input = Console.OpenStandardInput();
        using var reader = new StreamReader(input, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: true)
        { AutoFlush = true };
        bool? newlineDelimited = null;
        string? sessionId = null;

        while (true)
        {
            var (body, newline) = await ReadMessageAsync(reader, newlineDelimited, CancellationToken.None)
                .ConfigureAwait(false);
            if (body is null) break;
            newlineDelimited ??= newline;

            using var request = new HttpRequestMessage(HttpMethod.Post, options.McpEndpoint);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false); }
            catch (HttpRequestException ex)
            {
                await WriteAsync(writer, BuildTransportError(body, ex.Message), newlineDelimited.Value).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
                    sessionId = values.FirstOrDefault() ?? sessionId;

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted && string.IsNullOrWhiteSpace(content))
                    continue;
                if (!response.IsSuccessStatusCode)
                    content = BuildTransportError(body, $"HTTP {(int)response.StatusCode}: {content}");
                else if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    content = ParseSse(content);

                if (!string.IsNullOrWhiteSpace(content))
                    await WriteAsync(writer, content, newlineDelimited.Value).ConfigureAwait(false);
            }
        }

        if (sessionId is not null)
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, options.McpEndpoint);
            delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            try { using var _ = await client.SendAsync(delete).ConfigureAwait(false); } catch { }
        }
        return 0;
    }

    private static string Version => typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "2.8.0-mgs.8";

    private static async Task<bool> IsHealthyAsync(HttpClient client, Uri health)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await client.GetAsync(health, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task WaitForHealthAsync(HttpClient client, Uri health, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsHealthyAsync(client, health).ConfigureAwait(false)) return;
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private static void TryStartDaemon(ProxyOptions options)
    {
        var daemon = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "MGsCodeMap.Daemon.exe" : "MGsCodeMap.Daemon");
        if (!File.Exists(daemon)) return;
        var start = new ProcessStartInfo(daemon) { UseShellExecute = false, CreateNoWindow = true };
        if (options.ConfigPath is not null)
        {
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(options.ConfigPath);
        }
        Process.Start(start)?.Dispose();
    }

    private static string ParseSse(string body)
    {
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("data:", StringComparison.Ordinal)) return line[5..].TrimStart();
        return body;
    }

    private static string BuildTransportError(string request, string message)
    {
        JsonNode? id = null;
        try { id = (JsonNode.Parse(request) as JsonObject)?["id"]?.DeepClone(); } catch { }
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message },
        }.ToJsonString();
    }

    private static async Task WriteAsync(StreamWriter writer, string json, bool newlineDelimited)
    {
        if (newlineDelimited) await writer.WriteLineAsync(json).ConfigureAwait(false);
        else
        {
            await writer.WriteAsync($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n").ConfigureAwait(false);
            await writer.WriteAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(string? Body, bool IsNewline)> ReadMessageAsync(
        StreamReader reader, bool? knownFormat, CancellationToken ct)
    {
        var firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (firstLine is null) return (null, false);
        if (knownFormat == true || firstLine.StartsWith("{", StringComparison.Ordinal)) return (firstLine, true);

        var contentLength = -1;
        if (firstLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(firstLine.AsSpan("Content-Length:".Length).Trim(), out var firstLength))
            contentLength = firstLength;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return (null, false);
            if (line.Length == 0) break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var length)) contentLength = length;
        }
        if (contentLength <= 0 || contentLength > MaxContentLength) return (null, false);
        var buffer = new char[contentLength];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (count == 0) return (null, false);
            read += count;
        }
        return (new string(buffer), false);
    }

    private sealed record ProxyOptions(Uri McpEndpoint, Uri HealthEndpoint, string? ConfigPath, bool StartDaemon)
    {
        public static ProxyOptions Resolve(string[] args)
        {
            var configArg = Value(args, "--config");
            var configPath = configArg is null ? Path.Combine(AppContext.BaseDirectory, "codemap.json")
                : Path.GetFullPath(Path.IsPathRooted(configArg) ? configArg : Path.Combine(AppContext.BaseDirectory, configArg));
            var host = "127.0.0.1";
            var port = 5137;
            var mcpPath = "/mcp";
            var healthPath = "/health";
            if (File.Exists(configPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (TryProperty(document.RootElement, "server", out var server))
                {
                    if (TryProperty(server, "host", out var h)) host = h.GetString() ?? host;
                    if (TryProperty(server, "port", out var p)) port = p.GetInt32();
                    if (TryProperty(server, "mcpPath", out var m)) mcpPath = m.GetString() ?? mcpPath;
                    if (TryProperty(server, "healthPath", out var hp)) healthPath = hp.GetString() ?? healthPath;
                }
            }
            var endpointArg = Value(args, "--endpoint");
            var mcp = endpointArg is null ? new Uri($"http://{host}:{port}{NormalizePath(mcpPath)}") : new Uri(endpointArg);
            var health = new UriBuilder(mcp) { Path = NormalizePath(healthPath), Query = "" }.Uri;
            return new ProxyOptions(mcp, health, File.Exists(configPath) ? configPath : configArg, args.Contains("--start-daemon"));
        }

        private static bool TryProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
            value = default;
            return false;
        }

        private static string NormalizePath(string path) => path.StartsWith('/') ? path : "/" + path;

        private static string? Value(string[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{name} requires a value.");
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(name.Length + 1)..];
            }
            return null;
        }
    }
}
