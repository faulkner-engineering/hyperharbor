using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.VmConsole;

public sealed class ConsoleTicketStoreTests : IDisposable
{
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid VmId = Guid.NewGuid();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ConsoleTicketStore _tickets;

    public ConsoleTicketStoreTests()
    {
        _tickets = new ConsoleTicketStore(_time, TimeSpan.FromSeconds(120));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void IssuedTicket_IsValidForItsDeviceUserAndVm_RepeatedlyUntilExpiry()
    {
        var ticket = _tickets.Issue(DeviceId, UserId, VmId);

        _tickets.Validate(ticket.Value, DeviceId, UserId, VmId);
        _tickets.Validate(ticket.Value, DeviceId, UserId, VmId);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(120), ticket.ExpiresAt);

        _time.Advance(TimeSpan.FromSeconds(120));
        Assert.Throws<ConsoleTicketRejectedException>(() => _tickets.Validate(ticket.Value, DeviceId, UserId, VmId));
        Assert.Equal(0, _tickets.Count);
    }

    [Fact]
    public void Ticket_IsBoundToDeviceUserAndVm()
    {
        var ticket = _tickets.Issue(DeviceId, UserId, VmId);

        Assert.Throws<ConsoleTicketRejectedException>(() => _tickets.Validate(ticket.Value, Guid.NewGuid(), UserId, VmId));
        Assert.Throws<ConsoleTicketRejectedException>(() => _tickets.Validate(ticket.Value, DeviceId, Guid.NewGuid(), VmId));
        Assert.Throws<ConsoleTicketRejectedException>(() => _tickets.Validate(ticket.Value, DeviceId, UserId, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    public void MissingOrUnknownTicket_IsRejected(string? value)
    {
        _tickets.Issue(DeviceId, UserId, VmId);

        Assert.Throws<ConsoleTicketRejectedException>(() => _tickets.Validate(value, DeviceId, UserId, VmId));
    }

    [Fact]
    public void Tickets_AreRandomAndNotShownByToString()
    {
        var first = _tickets.Issue(DeviceId, UserId, VmId);
        var second = _tickets.Issue(DeviceId, UserId, VmId);

        Assert.NotEqual(first.Value, second.Value);
        Assert.True(first.Value.Length >= 43);
        Assert.DoesNotContain(first.Value, first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnpairingTheDevice_RevokesItsTickets()
    {
        var users = new UserStore(_directory);
        var user = users.GetOrCreateDefault();
        var devices = new PairedDeviceStore(_directory, users);
        var device = devices.Add(user.UserId, "Laptop", new string('A', 64), _time.GetUtcNow());
        var other = devices.Add(user.UserId, "Desktop", new string('B', 64), _time.GetUtcNow());
        var tickets = new ConsoleTicketStore(_time, TimeSpan.FromSeconds(120), devices);
        var revoked = tickets.Issue(device.DeviceId, user.UserId, VmId);
        var kept = tickets.Issue(other.DeviceId, user.UserId, VmId);

        devices.Remove(device.DeviceId);

        Assert.Throws<ConsoleTicketRejectedException>(() => tickets.Validate(revoked.Value, device.DeviceId, user.UserId, VmId));
        tickets.Validate(kept.Value, other.DeviceId, user.UserId, VmId);
    }
}
