namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Models;
using FluentAssertions;

public sealed class IndexMemoryTelemetryTests
{
    [Fact]
    public async Task Complete_ReturnsPeaksAndConfiguredLimits()
    {
        IndexMemoryTelemetry.MemorySnapshot? phaseSnapshot = null;
        await using var telemetry = IndexMemoryTelemetry.Start((phase, snapshot) =>
        {
            if (phase == "test-phase") phaseSnapshot = snapshot;
        });

        IndexMemoryTelemetry.MarkPhase("test-phase");
        var usage = telemetry.Complete(maxParallelProjects: 2, maxConcurrentIndexes: 1);

        phaseSnapshot.Should().NotBeNull();
        usage.PeakWorkingSetBytes.Should().BeGreaterThan(0);
        usage.PeakPrivateMemoryBytes.Should().BeGreaterThan(0);
        usage.PeakManagedHeapBytes.Should().BeGreaterThan(0);
        usage.MaxParallelProjects.Should().Be(2);
        usage.MaxConcurrentIndexes.Should().Be(1);
    }
}
