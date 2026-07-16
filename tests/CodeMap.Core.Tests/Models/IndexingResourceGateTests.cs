namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Models;
using FluentAssertions;

public sealed class IndexingResourceGateTests
{
    [Fact]
    public async Task AcquireAsync_DefaultLimit_SerializesFullIndexes()
    {
        using var gate = new IndexingResourceGate();
        var active = 0;
        var peak = 0;

        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            using (await gate.AcquireAsync())
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref peak, current);
                await Task.Delay(20);
                Interlocked.Decrement(ref active);
            }
        });

        await Task.WhenAll(tasks);

        peak.Should().Be(1);
    }

    [Fact]
    public void Constructor_ZeroLimit_IsRejected()
    {
        var act = () => new IndexingResourceGate(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
