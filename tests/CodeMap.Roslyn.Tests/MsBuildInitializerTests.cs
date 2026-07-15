namespace CodeMap.Roslyn.Tests;

using FluentAssertions;

public class MsBuildInitializerTests
{
    [Fact]
    public void SelectInstance_Automatic_PrefersVisualStudioThenHighestVersion()
    {
        MsBuildInstanceInfo[] instances =
        [
            new("SDK", new Version(19, 0), @"C:\sdk", "DotNetSdk"),
            new("VS 2022", new Version(17, 14), @"C:\vs17", "VisualStudio17"),
            new("VS 2026", new Version(18, 7), @"C:\vs18", "VisualStudio18"),
        ];

        var selected = MsBuildInitializer.SelectInstance(instances, requestedPath: null);

        selected.Should().Be(instances[2]);
    }

    [Fact]
    public void SelectInstance_ExplicitPath_SelectsExactInstance()
    {
        MsBuildInstanceInfo[] instances =
        [
            new("VS 2022", new Version(17, 14), Path.GetFullPath(@"C:\vs17"), "VisualStudio17"),
            new("VS 2026", new Version(18, 7), Path.GetFullPath(@"C:\vs18"), "VisualStudio18"),
        ];

        var selected = MsBuildInitializer.SelectInstance(instances, @"C:\vs17");

        selected.Should().Be(instances[0]);
    }

    [Fact]
    public void SelectInstance_UnknownExplicitPath_ReturnsNull()
    {
        var selected = MsBuildInitializer.SelectInstance([], Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        selected.Should().BeNull();
    }

    [Fact]
    public void EnsureRegistered_CalledMultipleTimes_DoesNotThrow()
    {
        // Call multiple times — should be idempotent
        var act = () =>
        {
            MsBuildInitializer.EnsureRegistered();
            MsBuildInitializer.EnsureRegistered();
            MsBuildInitializer.EnsureRegistered();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureRegistered_AfterCall_MSBuildIsRegistered()
    {
        MsBuildInitializer.EnsureRegistered();
        Microsoft.Build.Locator.MSBuildLocator.IsRegistered.Should().BeTrue();
    }
}
