namespace CodeMap.Integration.Tests.EndToEnd;

using System.Diagnostics;
using FluentAssertions;

/// <summary>Static subprocess checks for the windowless Task Scheduler host.</summary>
[Trait("Category", "Integration")]
public sealed class TaskHostSubprocessTests
{
    private static readonly string TaskHostDll =
        Path.Combine(AppContext.BaseDirectory, "MGsCodeMap.TaskHost.dll");

    [Fact]
    public async Task TaskHost_VersionFlag_PrintsVersionAndExits()
    {
        File.Exists(TaskHostDll).Should().BeTrue();
        using var process = Start("--version");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0);
        output.Trim().Should().Be("MGsCodeMapMCP Task Host 2.8.0-mgs.7");
    }

    [Fact]
    public void TaskHostDependencyGraph_DoesNotContainServerAssemblies()
    {
        var dependencies = File.ReadAllText(Path.ChangeExtension(TaskHostDll, ".deps.json"));
        dependencies.Should().NotContain("CodeMap.Core");
        dependencies.Should().NotContain("CodeMap.Daemon");
        dependencies.Should().NotContain("CodeMap.Mcp");
        dependencies.Should().NotContain("CodeMap.Roslyn");
        dependencies.Should().NotContain("CodeMap.Storage");
        dependencies.Should().NotContain("Microsoft.CodeAnalysis");
        dependencies.Should().NotContain("Microsoft.Build");
    }

    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(TaskHostDll);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start task host test process.");
    }
}
