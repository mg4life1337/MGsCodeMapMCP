namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Models;
using FluentAssertions;

public sealed class ConfigLoadingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ConfigLoadingTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Resolve_MissingConfig_UsesExecutableRelativeDefaults()
    {
        var result = RuntimeConfiguration.Resolve([], _tempDir, _ => null);

        result.ConfigPath.Should().Be(Path.Combine(_tempDir, "codemap.json"));
        result.DataDirectory.Should().Be(Path.Combine(_tempDir, "data"));
        result.LogDirectory.Should().Be(Path.Combine(_tempDir, "logs"));
        Directory.Exists(result.DataDirectory).Should().BeTrue();
        Directory.Exists(result.LogDirectory).Should().BeTrue();
    }

    [Fact]
    public void Resolve_CamelCaseConfig_ResolvesRelativeToConfigFile()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "custom.json");
        File.WriteAllText(configPath, """
            {
              "dataDirectory": ".\\portable-data",
              "logDirectory": ".\\portable-logs",
              "logLevel": "Debug",
              "msBuildPath": ".\\msbuild"
            }
            """);

        var result = RuntimeConfiguration.Resolve(
            ["--config", configPath], _tempDir, _ => null);

        result.DataDirectory.Should().Be(Path.Combine(configDir, "portable-data"));
        result.LogDirectory.Should().Be(Path.Combine(configDir, "portable-logs"));
        result.MsBuildPath.Should().Be(Path.Combine(configDir, "msbuild"));
        result.Config.LogLevel.Should().Be("Debug");
    }

    [Fact]
    public void Resolve_CommandLineOverridesEnvironmentAndConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "codemap.json"), """
            { "dataDirectory": "config-data", "logDirectory": "config-logs" }
            """);
        var environment = new Dictionary<string, string>
        {
            ["CODEMAP_DATA_DIR"] = "env-data",
            ["CODEMAP_LOG_DIR"] = "env-logs",
        };

        var result = RuntimeConfiguration.Resolve(
            ["--data-dir", "cli-data", "--log-dir=cli-logs"],
            _tempDir,
            name => environment.GetValueOrDefault(name));

        result.DataDirectory.Should().Be(Path.Combine(_tempDir, "cli-data"));
        result.LogDirectory.Should().Be(Path.Combine(_tempDir, "cli-logs"));
    }

    [Fact]
    public void Resolve_EnvironmentOverridesConfigAndIsExecutableRelative()
    {
        File.WriteAllText(Path.Combine(_tempDir, "codemap.json"), """
            { "dataDirectory": "config-data", "logDirectory": "config-logs" }
            """);

        var result = RuntimeConfiguration.Resolve(
            [],
            _tempDir,
            name => name switch
            {
                "CODEMAP_DATA_DIR" => "env-data",
                "CODEMAP_LOG_DIR" => "env-logs",
                _ => null,
            });

        result.DataDirectory.Should().Be(Path.Combine(_tempDir, "env-data"));
        result.LogDirectory.Should().Be(Path.Combine(_tempDir, "env-logs"));
    }

    [Fact]
    public void LoadConfig_MalformedJson_ReturnsUnderstandableError()
    {
        var configPath = Path.Combine(_tempDir, "codemap.json");
        File.WriteAllText(configPath, "{{not-json}}");

        var act = () => RuntimeConfiguration.LoadConfig(configPath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{configPath}*");
    }

    [Fact]
    public void LoadConfig_RollingRepository_ReadsNewOptions()
    {
        var configPath = Path.Combine(_tempDir, "codemap.json");
        File.WriteAllText(configPath, """
            {
              "repositories": [
                {
                  "root": ".\\repositories\\sample",
                  "discoverSolutions": true,
                  "defaultSolution": "src\\Primary.slnx",
                  "indexMode": "rollingBranch",
                  "updateStrategy": "incremental",
                  "checkAllSolutions": true,
                  "skipUnaffectedSolutions": true,
                  "servePreviousIndexWhileUpdating": true,
                  "retentionDays": 14,
                  "maxRollingBranches": 4,
                  "fullRebuildChangeThreshold": 1200
                }
              ]
            }
            """);

        var repository = RuntimeConfiguration.LoadConfig(configPath).Repositories.Should().ContainSingle().Subject;

        repository.DefaultSolution.Should().Be("src\\Primary.slnx");
        repository.IndexMode.Should().Be("rollingBranch");
        repository.UpdateStrategy.Should().Be("incremental");
        repository.CheckAllSolutions.Should().BeTrue();
        repository.SkipUnaffectedSolutions.Should().BeTrue();
        repository.ServePreviousIndexWhileUpdating.Should().BeTrue();
        repository.RetentionDays.Should().Be(14);
        repository.MaxRollingBranches.Should().Be(4);
        repository.FullRebuildChangeThreshold.Should().Be(1200);
    }

    [Fact]
    public void LoadConfig_ExistingRepository_KeepsCommitModeDefaults()
    {
        var configPath = Path.Combine(_tempDir, "codemap.json");
        File.WriteAllText(configPath, """
            { "repositories": [{ "root": ".\\repositories\\sample" }] }
            """);

        var repository = RuntimeConfiguration.LoadConfig(configPath).Repositories.Should().ContainSingle().Subject;

        repository.IndexMode.Should().Be("commit");
        repository.UpdateStrategy.Should().Be("full");
    }

    [Fact]
    public void LoadConfig_IndexingResources_ReadsAllOptions()
    {
        var configPath = Path.Combine(_tempDir, "codemap.json");
        File.WriteAllText(configPath, """
            {
              "indexingResources": {
                "maxConcurrentIndexes": 2,
                "maxParallelProjects": 3,
                "incrementalSolutionCacheSize": 2,
                "incrementalSolutionCacheIdleMinutes": 10,
                "memoryTelemetry": false
              }
            }
            """);

        var resources = RuntimeConfiguration.LoadConfig(configPath).IndexingResources;

        resources.Should().NotBeNull();
        resources!.MaxConcurrentIndexes.Should().Be(2);
        resources.MaxParallelProjects.Should().Be(3);
        resources.IncrementalSolutionCacheSize.Should().Be(2);
        resources.IncrementalSolutionCacheIdleMinutes.Should().Be(10);
        resources.MemoryTelemetry.Should().BeFalse();
    }

    [Theory]
    [InlineData("maxConcurrentIndexes", 0)]
    [InlineData("maxParallelProjects", 0)]
    [InlineData("incrementalSolutionCacheSize", 0)]
    [InlineData("incrementalSolutionCacheIdleMinutes", 0)]
    public void LoadConfig_InvalidIndexingResource_RejectsValue(string property, int value)
    {
        var configPath = Path.Combine(_tempDir, "codemap.json");
        File.WriteAllText(configPath, $$"""
            { "indexingResources": { "{{property}}": {{value}} } }
            """);

        var act = () => RuntimeConfiguration.LoadConfig(configPath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*indexingResources.{property}*");
    }

    [Fact]
    public void LoadConfig_Server_ReadsLoopbackHttpOptions()
    {
        var configPath = Path.Combine(_tempDir, "codemap.json");
        File.WriteAllText(configPath, """
            {
              "server": {
                "host": "127.0.0.1",
                "port": 5199,
                "mcpPath": "/semantic-mcp",
                "healthPath": "/status",
                "shutdownTimeoutSeconds": 45
              }
            }
            """);

        var server = RuntimeConfiguration.LoadConfig(configPath).Server;

        server.Should().NotBeNull();
        server!.Port.Should().Be(5199);
        server.McpPath.Should().Be("/semantic-mcp");
        server.HealthPath.Should().Be("/status");
        server.ShutdownTimeoutSeconds.Should().Be(45);
    }

    [Fact]
    public void LoadConfig_RemoteHostWithoutOptIn_IsRejected()
    {
        var configPath = Path.Combine(_tempDir, "codemap.json");
        File.WriteAllText(configPath, """{ "server": { "host": "0.0.0.0" } }""");

        var act = () => RuntimeConfiguration.LoadConfig(configPath);

        act.Should().Throw<InvalidOperationException>().WithMessage("*loopback*");
    }

    [Fact]
    public void CodeMapConfig_DefaultRecord_HasSafeDefaults()
    {
        var config = new CodeMapConfig();

        config.LogLevel.Should().Be("Information");
        config.SharedCacheDir.Should().BeNull();
        config.DataDirectory.Should().BeNull();
        config.LogDirectory.Should().BeNull();
        config.MsBuildPath.Should().BeNull();
        config.RepositoryRoots.Should().BeNull();
        config.Repositories.Should().BeNull();
        config.IndexingResources.Should().BeNull();

        var server = config.Server ?? new ServerConfig();
        server.Host.Should().Be("127.0.0.1");
        server.Port.Should().Be(5137);
        server.AllowRemote.Should().BeFalse();
        server.SingleInstance.Should().BeTrue();

        var resources = config.IndexingResources ?? new IndexingResourceConfig();
        resources.MaxConcurrentIndexes.Should().Be(1);
        resources.MaxParallelProjects.Should().Be(2);
        resources.IncrementalSolutionCacheSize.Should().Be(1);
        resources.IncrementalSolutionCacheIdleMinutes.Should().Be(5);
        resources.MemoryTelemetry.Should().BeTrue();
    }
}
