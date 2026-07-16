namespace CodeMap.Daemon.Tests;

using FluentAssertions;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mgscodemap-lock-" + Guid.NewGuid().ToString("N"));

    public SingleInstanceTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public void SameDataDirectory_AllowsOnlyOneWriter()
    {
        var runtime = RuntimeConfiguration.Resolve(["--data-dir", "data", "--log-dir", "logs"], _tempDir, _ => null);

        DaemonInstanceLock.TryAcquire(runtime, "test", out var first, out _).Should().BeTrue();
        using (first)
        {
            DaemonInstanceLock.TryAcquire(runtime, "test", out var second, out var existing).Should().BeFalse();
            second.Should().BeNull();
            existing.Should().NotBeNull();
            existing!.ProcessId.Should().Be(Environment.ProcessId);
        }

        File.Exists(Path.Combine(runtime.DataDirectory, "daemon.lock")).Should().BeFalse();
    }

    [Fact]
    public void DifferentDataDirectories_AreIndependent()
    {
        var one = RuntimeConfiguration.Resolve(["--data-dir", "one", "--log-dir", "logs-one"], _tempDir, _ => null);
        var two = RuntimeConfiguration.Resolve(["--data-dir", "two", "--log-dir", "logs-two"], _tempDir, _ => null);

        DaemonInstanceLock.TryAcquire(one, "test", out var first, out _).Should().BeTrue();
        DaemonInstanceLock.TryAcquire(two, "test", out var second, out _).Should().BeTrue();
        using (first)
        using (second) { }
    }

    [Fact]
    public void StaleMetadata_IsReplacedAfterOwnershipIsAvailable()
    {
        var runtime = RuntimeConfiguration.Resolve(["--data-dir", "stale", "--log-dir", "stale-logs"], _tempDir, _ => null);
        var lockPath = Path.Combine(runtime.DataDirectory, "daemon.lock");
        File.WriteAllText(lockPath, "{\"processId\":2147483647}");

        DaemonInstanceLock.TryAcquire(runtime, "current", out var current, out _).Should().BeTrue();
        using (current)
        {
            var metadata = DaemonInstanceLock.Read(lockPath);
            metadata.Should().NotBeNull();
            metadata!.ProcessId.Should().Be(Environment.ProcessId);
            metadata.Version.Should().Be("current");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
