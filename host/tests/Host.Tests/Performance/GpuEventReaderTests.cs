using HyperHarbor.Host.Core.Performance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Performance;

public sealed class GpuEventReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Summarize_GroupsByProviderAndEvent_NewestFirst_WithTheLatestMessage()
    {
        var warnings = GpuDriverEvents.Summarize(
            [
                new GpuEvent("nvlddmkm", 153, Now.AddDays(-3), "Older reset."),
                new GpuEvent("nvlddmkm", 153, Now.AddHours(-2), "The GPU was reset."),
                new GpuEvent("Display", 4101, Now.AddHours(-1), null),
                new GpuEvent("amdkmdag", 4101, Now.AddDays(-1), "AMD driver error."),
            ],
            Now);

        Assert.Collection(
            warnings,
            display =>
            {
                Assert.Equal(("Display", 4101, 1), (display.Provider, display.EventId, display.Count));
                Assert.Equal("The display driver stopped responding and has recovered.", display.Message);
            },
            nvidia =>
            {
                Assert.Equal(("nvlddmkm", 153, 2), (nvidia.Provider, nvidia.EventId, nvidia.Count));
                Assert.Equal(Now.AddHours(-2), nvidia.LastSeen);
                Assert.Equal("The GPU was reset.", nvidia.Message);
            },
            amd => Assert.Equal("amdkmdag", amd.Provider));
    }

    [Fact]
    public void Summarize_LeavesOutOldEvents_OtherProviders_AndOtherDisplayEvents()
    {
        var warnings = GpuDriverEvents.Summarize(
            [
                new GpuEvent("nvlddmkm", 14, Now.AddDays(-8), "Last week."),
                new GpuEvent("Display", 4100, Now.AddHours(-1), "Not a timeout."),
                new GpuEvent("Disk", 7, Now.AddHours(-1), "Bad block."),
                new GpuEvent("igfxn", 1, Now.AddHours(-1), "Intel."),
            ],
            Now);

        var warning = Assert.Single(warnings);
        Assert.Equal("igfxn", warning.Provider);
    }

    [Fact]
    public void Describe_PutsTheMessageOnOneLine_AndCapsIt()
    {
        Assert.Equal("Line one. Line two.", GpuDriverEvents.Describe(new GpuEvent("nvlddmkm", 13, Now, " Line one.\r\n\tLine two.\u0007 ")));

        var capped = GpuDriverEvents.Describe(new GpuEvent("nvlddmkm", 13, Now, new string('x', 1000)));
        Assert.Equal(GpuDriverEvents.MaxMessageLength, capped.Length);
        Assert.EndsWith("…", capped, StringComparison.Ordinal);

        Assert.Equal("The nvlddmkm driver reported event 13.", GpuDriverEvents.Describe(new GpuEvent("nvlddmkm", 13, Now, null)));
    }

    [Fact]
    public void Reader_ReadsTheLogAtMostOnceEveryFiveMinutes()
    {
        var source = new FakeGpuEventSource();
        source.Events.Add(new GpuEvent("Display", 4101, Now.AddHours(-1), null));
        var time = new FakeTimeProvider(Now);
        var reader = new GpuEventReader(source, time);

        Assert.Single(reader.Warnings());
        source.Events.Add(new GpuEvent("nvlddmkm", 153, Now, "Reset."));
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Single(reader.Warnings());
        Assert.Equal(1, source.Reads);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, reader.Warnings().Count);
        Assert.Equal(2, source.Reads);
    }

    /// <summary>Reads this PC's System log, which proves the query is one the event log accepts.</summary>
    [LocalHardwareFact]
    public void EventLogSource_AcceptsTheQuery()
    {
        var events = new EventLogGpuEventSource(NullLogger<EventLogGpuEventSource>.Instance).Read(DateTimeOffset.UtcNow - GpuDriverEvents.Window);

        Assert.All(events, record => Assert.True(GpuDriverEvents.IsGpuEvent(record), $"{record.Provider} {record.EventId}"));

        // A malformed query throws EventLogInvalidDataException, which the source would only log.
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
            new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName, GpuDriverEvents.Query(GpuDriverEvents.Window)));
        using var first = reader.ReadEvent();
    }
}
