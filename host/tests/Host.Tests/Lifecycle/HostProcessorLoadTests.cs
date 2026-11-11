using HyperHarbor.Host.Core.Lifecycle;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class HostProcessorLoadTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-11T12:00:00Z"));
    private readonly Queue<SystemTimes?> _readings = new();

    private HostProcessorLoad Create(int? cores = 8) => new(_time, () => _readings.Dequeue(), () => cores);

    [Fact]
    public void Usage_IsTheBusyShareOfKernelAndUserTime()
    {
        // Kernel 800 (750 of it idle) and user 200: 250 of 1000 units busy.
        Assert.Equal(25.0, HostProcessorLoad.Usage(new SystemTimes(0, 0, 0), new SystemTimes(750, 800, 200)));
    }

    [Fact]
    public void Usage_IsNullWithoutElapsedTime()
    {
        Assert.Null(HostProcessorLoad.Usage(new SystemTimes(10, 20, 30), new SystemTimes(10, 20, 30)));
    }

    [Fact]
    public async Task FirstReading_WaitsForASecondSample()
    {
        var load = Create();
        _readings.Enqueue(new SystemTimes(0, 0, 0));
        _readings.Enqueue(new SystemTimes(900, 1000, 0));

        var reading = load.ReadUsagePercentAsync(CancellationToken.None);
        Assert.False(reading.IsCompleted);
        _time.Advance(HostProcessorLoad.MinimumSpan);

        Assert.Equal(10.0, await reading);
        Assert.Empty(_readings);
    }

    [Fact]
    public async Task ReadingSoonAfterThePrevious_UsesItWithoutWaiting()
    {
        var load = Create();
        _readings.Enqueue(new SystemTimes(0, 0, 0));
        _readings.Enqueue(new SystemTimes(900, 1000, 0));
        var first = load.ReadUsagePercentAsync(CancellationToken.None);
        _time.Advance(HostProcessorLoad.MinimumSpan);
        await first;

        _time.Advance(TimeSpan.FromSeconds(5));
        _readings.Enqueue(new SystemTimes(1400, 2000, 0));
        var second = load.ReadUsagePercentAsync(CancellationToken.None);

        Assert.True(second.IsCompleted);
        Assert.Equal(50.0, await second);
    }

    [Fact]
    public async Task ReadingLongAfterThePrevious_SamplesAgain()
    {
        var load = Create();
        _readings.Enqueue(new SystemTimes(0, 0, 0));
        _readings.Enqueue(new SystemTimes(900, 1000, 0));
        var first = load.ReadUsagePercentAsync(CancellationToken.None);
        _time.Advance(HostProcessorLoad.MinimumSpan);
        await first;

        // An average over a minute is not current use.
        _time.Advance(TimeSpan.FromMinutes(1));
        _readings.Enqueue(new SystemTimes(10_000, 20_000, 0));
        _readings.Enqueue(new SystemTimes(10_000, 21_000, 0));
        var second = load.ReadUsagePercentAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        _time.Advance(HostProcessorLoad.MinimumSpan);

        Assert.Equal(100.0, await second);
    }

    [Fact]
    public async Task UnreadableTimes_GiveNull()
    {
        var load = Create();
        _readings.Enqueue(null);

        Assert.Null(await load.ReadUsagePercentAsync(CancellationToken.None));
    }

    [Fact]
    public void Cores_AreCountedOnce()
    {
        var calls = 0;
        var load = new HostProcessorLoad(_time, () => null, () => ++calls);

        Assert.Equal(1, load.PhysicalCoreCount);
        Assert.Equal(1, load.PhysicalCoreCount);
    }

    [Fact]
    public async Task WindowsReader_GivesPlausibleFigures()
    {
        var load = new HostProcessorLoad(TimeProvider.System);

        Assert.InRange(load.PhysicalCoreCount ?? 0, 1, Environment.ProcessorCount);
        Assert.InRange(await load.ReadUsagePercentAsync(CancellationToken.None) ?? -1, 0, 100);
    }
}
