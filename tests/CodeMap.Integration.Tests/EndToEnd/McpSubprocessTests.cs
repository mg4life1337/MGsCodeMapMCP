namespace CodeMap.Integration.Tests.EndToEnd;

using System.Diagnostics;
using FluentAssertions;

/// <summary>Subprocess checks for the lightweight STDIO-to-HTTP compatibility proxy.</summary>
[Trait("Category", "Integration")]
public sealed class McpSubprocessTests
{
    private static readonly string ProxyDll = Path.Combine(AppContext.BaseDirectory, "MGsCodeMap.Mcp.dll");

    [Fact]
    public void ProxyBinary_IsIncludedWithIntegrationBuild()
    {
        File.Exists(ProxyDll).Should().BeTrue();
    }

    [Fact]
    public async Task Proxy_VersionFlag_PrintsVersionAndExits()
    {
        using var process = Start("--version");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0);
        output.Trim().Should().Be("MGsCodeMapMCP 2.8.0-mgs.6");
    }

    [Fact]
    public void ProxyDependencyGraph_DoesNotContainHeavyServerAssemblies()
    {
        var depsPath = Path.ChangeExtension(ProxyDll, ".deps.json");
        var dependencies = File.ReadAllText(depsPath);

        dependencies.Should().NotContain("CodeMap.Roslyn");
        dependencies.Should().NotContain("CodeMap.Storage");
        dependencies.Should().NotContain("Microsoft.CodeAnalysis");
    }

    [Fact]
    public async Task Proxy_UnreachableDaemon_ReturnsClearError()
    {
        using var process = Start("--endpoint", "http://127.0.0.1:1/mcp");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(4);
        error.Should().Contain("daemon is not reachable");
    }

    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(ProxyDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start proxy test process.");
    }
}
