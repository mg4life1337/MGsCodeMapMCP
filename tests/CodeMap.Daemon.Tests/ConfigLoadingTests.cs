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
    public void CodeMapConfig_DefaultRecord_HasPortableDefaults()
    {
        var config = new CodeMapConfig();

        config.LogLevel.Should().Be("Information");
        config.SharedCacheDir.Should().BeNull();
        config.DataDirectory.Should().BeNull();
        config.LogDirectory.Should().BeNull();
        config.MsBuildPath.Should().BeNull();
        config.RepositoryRoots.Should().BeNull();
        config.Repositories.Should().BeNull();
    }
}
