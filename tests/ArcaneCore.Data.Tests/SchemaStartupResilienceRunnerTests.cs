using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The fault runner of <see cref="SchemaStartupResilienceTests"/>: its width comes from
/// <c>ARCANECORE_TEST_FAULT_PARALLELISM</c> (default 4), it really overlaps that many faults, never more, runs every
/// fault even after one fails, and rethrows the failure. No database is involved.
/// </summary>
public sealed class SchemaStartupResilienceRunnerTests
{
    [Theory]
    [InlineData(null, SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("four", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("0", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("-2", SchemaStartupResilienceTests.DefaultFaultParallelism)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("16", 16)]
    public void ReadFaultParallelism_TakesAPositiveInteger_OrTheDefault(string? value, int expected)
    {
        Assert.Equal(expected, SchemaStartupResilienceTests.ReadFaultParallelism(value));
    }

    [Fact]
    public void FaultParallelism_IsAtLeastOne()
    {
        Assert.True(SchemaStartupResilienceTests.FaultParallelism >= 1);
        Assert.Equal(4, SchemaStartupResilienceTests.DefaultFaultParallelism);
    }

    [Fact]
    public async Task RunFaultsAsync_OverlapsExactlyTheConfiguredWidth_RunsEveryFault_AndRethrowsTheFirstFailure()
    {
        const int parallelism = 3;
        const int count = 10;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        int running = 0;
        int peak = 0;
        int finished = 0;

        Task run = SchemaStartupResilienceTests.RunFaultsAsync(Enumerable.Range(1, count).ToList(), parallelism, async fault =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            do
            {
                seen = Volatile.Read(ref peak);
            }
            while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);

            try
            {
                // The first wave opens the gate only once the whole width is inside: a serial runner never gets here.
                if (Interlocked.Increment(ref started) == parallelism)
                {
                    gate.TrySetResult();
                }

                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Yield();
                if (fault == 5)
                {
                    throw new InvalidOperationException("fault 5 failed");
                }
            }
            finally
            {
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref finished);
            }
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("fault 5 failed", ex.Message);
        Assert.Equal(count, finished);
        Assert.Equal(parallelism, peak);
    }

    [Fact]
    public async Task RunFaultsAsync_RefusesAWidthBelowOne()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SchemaStartupResilienceTests.RunFaultsAsync(new[] { 1 }, 0, _ => Task.CompletedTask));
    }
}
