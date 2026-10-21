using HyperHarbor.Host.Core.Transfer;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Transfer;

public sealed class KeyProtectorHandoverTests
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly string Peer = KeyProtectorHandover.PeerGuardianName(Guid.Parse("9c8b7a65-4321-4fed-8cba-0987654321ab"));
    private static readonly byte[] PeerExport = [60, 120, 109, 108, 62];

    private readonly FakeKeyProtectorAccess _access = new();
    private readonly KeyProtectorHandover _handover;

    public KeyProtectorHandoverTests() => _handover = new KeyProtectorHandover(_access, NullLogger<KeyProtectorHandover>.Instance);

    [Fact]
    public async Task TheStepRuns_WithThePeerGranted_ThenTheOriginalIsBackAndThePeerIsGone()
    {
        byte[]? duringStep = null;

        var result = await _handover.WithPeerGrantedAsync(VmId, PeerExport, Peer, _ =>
        {
            duringStep = _access.Current;
            return Task.FromResult("exported");
        }, CancellationToken.None);

        Assert.Equal("exported", result);
        Assert.NotEqual(FakeKeyProtectorAccess.Original, duringStep);
        Assert.Equal(FakeKeyProtectorAccess.Original, _access.Current);
        Assert.Empty(_access.Guardians);
        Assert.Equal(["get", "grant", "set", "set-original", "get", "remove"], _access.Log);
    }

    [Fact]
    public async Task AFailedStep_StillPutsTheOriginalBack_AndItsErrorIsTheOneThrown()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _handover.WithPeerGrantedAsync<int>(
            VmId, PeerExport, Peer, _ => throw new InvalidOperationException("export failed"), CancellationToken.None));

        Assert.Equal("export failed", error.Message);
        Assert.Equal(FakeKeyProtectorAccess.Original, _access.Current);
        Assert.Empty(_access.Guardians);
    }

    [Fact]
    public async Task ACanceledStep_StillPutsTheOriginalBack()
    {
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _handover.WithPeerGrantedAsync(VmId, PeerExport, Peer, async token =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            return 0;
        }, cancel.Token));

        Assert.Equal(FakeKeyProtectorAccess.Original, _access.Current);
        Assert.Contains("remove", _access.Log);
    }

    [Fact]
    public async Task AFailedGrant_NeverRunsTheStep_AndRemovesThePeer()
    {
        _access.FailOn = "grant";
        var ran = false;

        await Assert.ThrowsAsync<IOException>(() => _handover.WithPeerGrantedAsync(VmId, PeerExport, Peer, _ =>
        {
            ran = true;
            return Task.FromResult(0);
        }, CancellationToken.None));

        Assert.False(ran);
        Assert.Equal(FakeKeyProtectorAccess.Original, _access.Current);
        Assert.Contains("remove", _access.Log);
    }

    [Fact]
    public async Task OriginalThatCannotBePutBack_IsATransferError_EvenAfterTheStepSucceeded()
    {
        _access.FailOn = "set-original";

        var error = await Assert.ThrowsAsync<TransferException>(() =>
            _handover.WithPeerGrantedAsync(VmId, PeerExport, Peer, _ => Task.FromResult(0), CancellationToken.None));

        Assert.Contains("could not be put back", error.Message, StringComparison.Ordinal);
        Assert.Contains("remove", _access.Log);
    }

    [Fact]
    public async Task OriginalThatReadsBackDifferently_IsATransferError()
    {
        _access.ReadBackAfterRestore = [9, 9, 9];

        await Assert.ThrowsAsync<TransferException>(() =>
            _handover.WithPeerGrantedAsync(VmId, PeerExport, Peer, _ => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public async Task AGuardianThatCannotBeRemoved_DoesNotFailTheTransfer()
    {
        _access.FailOn = "remove";

        var result = await _handover.WithPeerGrantedAsync(VmId, PeerExport, Peer, _ => Task.FromResult(7), CancellationToken.None);

        Assert.Equal(7, result);
        Assert.Equal(FakeKeyProtectorAccess.Original, _access.Current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(KeyProtectorHandover.MaxGuardianExportBytes + 1)]
    public async Task MissingOrOversizedGuardian_IsRefused_BeforeAnythingChanges(int size)
    {
        await Assert.ThrowsAsync<TransferException>(() =>
            _handover.WithPeerGrantedAsync(VmId, new byte[size], Peer, _ => Task.FromResult(0), CancellationToken.None));

        Assert.Empty(_access.Log);
    }

    [Fact]
    public async Task UnreadableKeyProtector_IsRefused_BeforeAnythingChanges()
    {
        _access.Current = [];

        await Assert.ThrowsAsync<TransferException>(() =>
            _handover.WithPeerGrantedAsync(VmId, PeerExport, Peer, _ => Task.FromResult(0), CancellationToken.None));

        Assert.Equal(["get"], _access.Log);
    }

    [Fact]
    public void PeerGuardianName_NamesThePeerHost()
    {
        Assert.Equal("HyperHarbor peer 9c8b7a65-4321-4fed-8cba-0987654321ab", Peer);
    }

    [Fact]
    public async Task TheHostsOwnGuardian_IsNeverRemoved()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new CimKeyProtectorAccess().RemoveGuardianAsync("UntrustedGuardian", CancellationToken.None));
    }
}
