using System.Security.Cryptography;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Transfer;

public sealed class TransferReceiverTests : IDisposable
{
    private const long Block = 4;
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly string Source = Convert.ToHexString(SHA256.HashData("source host"u8));
    private static readonly byte[] DiskBytes = "0123456789"u8.ToArray();
    private static readonly byte[] DefinitionBytes = "<vmcx/>"u8.ToArray();
    private static readonly string DefinitionKey = $"definition:{VmId:D}.vmcx";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-receiver-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeHyperVTransfer _hyperV = new() { MoveId = VmId };
    private readonly FakeKeyProtectorAccess _keys = new();
    private readonly IncomingTransferStore _store;
    private TransferReceiver _receiver;

    public TransferReceiverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _store = new IncomingTransferStore(Path.Combine(_root, "data"));
        _receiver = CreateReceiver();
    }

    private string VmFolder => Path.Combine(_root, "vms", "Web");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Offer_PreparesTheVmsOwnFolder_AndKeepsTheTicketOnlyInMemory()
    {
        var offer = await OfferAsync();

        Assert.Equal(43, offer.Ticket.Length);
        Assert.Equal(_keys.LocalGuardianExport, offer.Guardian);
        Assert.Equal(VmFolder, offer.Plan.VmFolder);
        Assert.True(Directory.Exists(Path.Combine(VmFolder, "Virtual Hard Disks")));
        Assert.DoesNotContain(offer.Ticket, offer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(offer.Ticket, await File.ReadAllTextAsync(Path.Combine(_root, "data", IncomingTransferStore.FileName)), StringComparison.Ordinal);
        Assert.True(_receiver.AnyActive);
    }

    [Fact]
    public async Task Offer_WithoutATpm_SendsNoGuardian()
    {
        var offer = await _receiver.OfferAsync(Request() with { Preview = Preview() with { TpmEnabled = false } }, UserId, CancellationToken.None);

        Assert.Null(offer.Guardian);
    }

    [Fact]
    public async Task Offer_ThisHostCannotTake_IsRefused_AndCreatesNothing()
    {
        var request = Request() with { Preview = Preview() with { ConfigurationVersion = "99.0" } };

        await Assert.ThrowsAsync<LifecycleValidationException>(() => _receiver.OfferAsync(request, UserId, CancellationToken.None));

        Assert.False(Directory.Exists(VmFolder));
        Assert.False(_receiver.AnyActive);
    }

    [Fact]
    public async Task Offer_WithWarnings_NeedsThemAcknowledged()
    {
        var request = Request() with { Mode = TransferMode.Copy, AcknowledgeWarnings = false };

        await Assert.ThrowsAsync<ResourceWarningsException>(() => _receiver.OfferAsync(request, UserId, CancellationToken.None));
        var offer = await _receiver.OfferAsync(request with { AcknowledgeWarnings = true }, UserId, CancellationToken.None);

        Assert.Equal("Web", offer.Plan.Name);
    }

    [Fact]
    public async Task Offer_OfAVmAlreadyBeingMovedHere_IsAConflict()
    {
        await OfferAsync();

        await Assert.ThrowsAsync<LifecycleConflictException>(() => _receiver.OfferAsync(Request(), UserId, CancellationToken.None));
    }

    [Fact]
    public async Task Offer_WithABadFingerprint_IsRefused()
    {
        await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _receiver.OfferAsync(Request() with { SourceFingerprint = "not hex" }, UserId, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticate_NeedsTheTicket_TheSourceCertificate_AndAnActiveTransfer()
    {
        var offer = await OfferAsync();
        var colons = string.Join(':', Enumerable.Range(0, 32).Select(i => Source.Substring(i * 2, 2))).ToLowerInvariant();

        Assert.Equal(new TransferPrincipal(offer.TransferId, UserId), _receiver.Authenticate(offer.TransferId, offer.Ticket, Source));
        Assert.NotNull(_receiver.Authenticate(offer.TransferId, offer.Ticket, colons));
        Assert.Null(_receiver.Authenticate(offer.TransferId, offer.Ticket + "x", Source));
        Assert.Null(_receiver.Authenticate(offer.TransferId, offer.Ticket, Convert.ToHexString(SHA256.HashData("other host"u8))));
        Assert.Null(_receiver.Authenticate(offer.TransferId, offer.Ticket, null));
        Assert.Null(_receiver.Authenticate(Guid.NewGuid(), offer.Ticket, Source));
        Assert.Null(_receiver.Authenticate(offer.TransferId, null, Source));
    }

    [Fact]
    public async Task Authenticate_FailsAfterTheIdleTimeout_AndEachRequestExtendsIt()
    {
        var offer = await OfferAsync();

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(_receiver.Authenticate(offer.TransferId, offer.Ticket, Source));
        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(_receiver.Authenticate(offer.TransferId, offer.Ticket, Source));
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(_receiver.Authenticate(offer.TransferId, offer.Ticket, Source));
    }

    [Fact]
    public async Task Disk_ArrivesInBlocks_AndIsMovedIntoPlaceWhenComplete()
    {
        var offer = await OfferAsync();
        var disk = Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx");

        await SendAsync(offer.TransferId, "disk:0", DiskBytes, 0);
        await SendAsync(offer.TransferId, "disk:0", DiskBytes, 4);
        Assert.False(File.Exists(disk));
        Assert.Equal(8, _receiver.Status(offer.TransferId).Files.Single().Received);
        var done = await SendAsync(offer.TransferId, "disk:0", DiskBytes, 8);

        Assert.True(done.IsComplete);
        Assert.Equal(DiskBytes, await File.ReadAllBytesAsync(disk));
        Assert.False(File.Exists(disk + TransferReceiver.PartialSuffix));
    }

    [Fact]
    public async Task BlockAtTheWrongOffset_SaysWhereTheNextBlockStarts()
    {
        var offer = await OfferAsync();
        await SendAsync(offer.TransferId, "disk:0", DiskBytes, 0);

        var error = await Assert.ThrowsAsync<TransferOffsetException>(() => SendAsync(offer.TransferId, "disk:0", DiskBytes, 8));

        Assert.Equal(4, error.Expected);
    }

    [Fact]
    public async Task BlockWithAWrongHash_IsDiscarded_AndTheTransferResumesFromTheLastGoodBlock()
    {
        var offer = await OfferAsync();
        await SendAsync(offer.TransferId, "disk:0", DiskBytes, 0);

        await Assert.ThrowsAsync<TransferException>(() => _receiver.ReceiveBlockAsync(
            offer.TransferId, "disk:0", 4, 4, DiskBytes.Length, SHA256.HashData("XXXX"u8), new MemoryStream("4567"u8.ToArray()), CancellationToken.None));
        Assert.Equal(4, _receiver.Status(offer.TransferId).Files.Single().Received);
        Assert.Equal(4, new FileInfo(Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx" + TransferReceiver.PartialSuffix)).Length);

        await SendAsync(offer.TransferId, "disk:0", DiskBytes, 4);
        await SendAsync(offer.TransferId, "disk:0", DiskBytes, 8);
        Assert.Equal(DiskBytes, await File.ReadAllBytesAsync(Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx")));
    }

    [Theory]
    [InlineData("short body")]
    [InlineData("long body")]
    [InlineData("wrong length")]
    [InlineData("changed size")]
    [InlineData("unknown file")]
    [InlineData("bad definition name")]
    [InlineData("empty definition")]
    public async Task MalformedBlocks_AreRefused(string kind)
    {
        var offer = await OfferAsync();
        var id = offer.TransferId;
        var block = DiskBytes[..4];
        Task Send(string key, long length, long size, byte[] body) =>
            _receiver.ReceiveBlockAsync(id, key, 0, length, size, SHA256.HashData(body), new MemoryStream(body), CancellationToken.None);

        await Assert.ThrowsAsync<TransferException>(() => kind switch
        {
            "short body" => _receiver.ReceiveBlockAsync(id, "disk:0", 0, 4, 10, SHA256.HashData(block), new MemoryStream(block[..3]), CancellationToken.None),
            "long body" => _receiver.ReceiveBlockAsync(id, "disk:0", 0, 4, 10, SHA256.HashData(block), new MemoryStream(DiskBytes[..5]), CancellationToken.None),
            "wrong length" => Send("disk:0", 3, 10, block[..3]),
            "changed size" => Send("disk:0", 4, 11, block),
            "unknown file" => Send("disk:1", 4, 10, block),
            "bad definition name" => Send(@"definition:..\..\evil.vmcx", 4, 4, block),
            "empty definition" => Send(DefinitionKey, 0, 0, []),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
        Assert.All(_receiver.Status(id).Files, file => Assert.Equal(0, file.Received));
    }

    [Fact]
    public async Task CompleteFile_TakesNoMoreBlocks()
    {
        var offer = await OfferAsync();
        await SendFileAsync(offer.TransferId, "disk:0", DiskBytes);

        await Assert.ThrowsAsync<TransferException>(() => SendAsync(offer.TransferId, "disk:0", DiskBytes, 10));
    }

    [Fact]
    public async Task Import_NeedsEveryFile()
    {
        var offer = await OfferAsync();
        await SendFileAsync(offer.TransferId, DefinitionKey, DefinitionBytes);

        await Assert.ThrowsAsync<TransferException>(() => _receiver.ImportAsync(offer.TransferId, CancellationToken.None));
        Assert.Empty(_hyperV.Log);
    }

    [Fact]
    public async Task Import_OfAMove_KeepsTheId_AndDeletesTheStagingFolder()
    {
        var offer = await ReceiveAllAsync();

        var result = await _receiver.ImportAsync(offer.TransferId, CancellationToken.None);

        Assert.Equal(new TransferImportResult(VmId, "Web"), result);
        Assert.Equal([$"import same {VmId:D}.vmcx", "fix", "validate", "realize"], _hyperV.Log);
        Assert.Equal(IncomingTransferState.Imported, _receiver.Status(offer.TransferId).State);
        Assert.False(Directory.Exists(Path.Combine(VmFolder, TransferReceiver.StagingFolderName)));
        Assert.True(File.Exists(Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx")));
    }

    [Fact]
    public async Task Import_OfACopy_AsksForANewId()
    {
        var offer = await _receiver.OfferAsync(Request() with { Mode = TransferMode.Copy, AcknowledgeWarnings = true }, UserId, CancellationToken.None);
        await SendFileAsync(offer.TransferId, "disk:0", DiskBytes);
        await SendFileAsync(offer.TransferId, DefinitionKey, DefinitionBytes);

        var result = await _receiver.ImportAsync(offer.TransferId, CancellationToken.None);

        Assert.NotEqual(VmId, result.VmId);
        Assert.StartsWith("import new", _hyperV.Log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRealize_RemovesTheImportedVm_KeepsTheFiles_AndCanBeRetried()
    {
        var offer = await ReceiveAllAsync();
        _hyperV.FailOn = "realize";
        _hyperV.OnRealize = id => _inventory.Vms.Add(FakeVmInventory.CreateVm(id, "Web", VmState.Off));

        await Assert.ThrowsAsync<TransferException>(() => _receiver.ImportAsync(offer.TransferId, CancellationToken.None));

        Assert.Contains("delete-planned", _hyperV.Log);
        Assert.Equal([VmId], _storage.DeletedVms);
        Assert.Equal(IncomingTransferState.Receiving, _receiver.Status(offer.TransferId).State);
        Assert.True(File.Exists(Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx")));

        _hyperV.FailOn = null;
        _inventory.Vms.Clear();
        await _receiver.ImportAsync(offer.TransferId, CancellationToken.None);
        Assert.Equal(IncomingTransferState.Imported, _receiver.Status(offer.TransferId).State);
    }

    [Fact]
    public async Task MoveWhoseDefinitionIsAnotherVm_IsRefused_AndRemoved()
    {
        var offer = await ReceiveAllAsync();
        _hyperV.ImportedId = Guid.NewGuid();

        await Assert.ThrowsAsync<TransferException>(() => _receiver.ImportAsync(offer.TransferId, CancellationToken.None));

        Assert.Equal([$"import same {VmId:D}.vmcx", "delete-planned"], _hyperV.Log);
    }

    [Fact]
    public async Task Complete_PutsTheStartupActionBack_AndEndsTheTicket()
    {
        var offer = await ReceiveAllAsync();
        await _receiver.ImportAsync(offer.TransferId, CancellationToken.None);

        await _receiver.CompleteAsync(offer.TransferId, CancellationToken.None);

        Assert.Equal((ushort)3, _hyperV.StartupActions[VmId]);
        Assert.Null(_receiver.Authenticate(offer.TransferId, offer.Ticket, Source));
        Assert.False(_receiver.AnyActive);
        Assert.True(File.Exists(Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx")));
    }

    [Fact]
    public async Task Complete_BeforeTheImport_IsRefused()
    {
        var offer = await ReceiveAllAsync();

        await Assert.ThrowsAsync<TransferException>(() => _receiver.CompleteAsync(offer.TransferId, CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_BeforeTheImport_DeletesOnlyTheVmsOwnFolder()
    {
        var neighbour = Path.Combine(_root, "vms", "Other");
        Directory.CreateDirectory(neighbour);
        var offer = await OfferAsync();
        await SendAsync(offer.TransferId, "disk:0", DiskBytes, 0);

        Assert.True(await _receiver.CancelAsync(offer.TransferId, CancellationToken.None));

        Assert.False(Directory.Exists(VmFolder));
        Assert.True(Directory.Exists(neighbour));
        Assert.False(_receiver.AnyActive);
        Assert.Throws<TransferNotFoundException>(() => _receiver.Status(offer.TransferId));
    }

    [Fact]
    public async Task Cancel_AfterTheImport_KeepsTheVm()
    {
        var offer = await ReceiveAllAsync();
        await _receiver.ImportAsync(offer.TransferId, CancellationToken.None);

        Assert.False(await _receiver.CancelAsync(offer.TransferId, CancellationToken.None));

        Assert.DoesNotContain("delete-planned", _hyperV.Log);
        Assert.Empty(_storage.DeletedVms);
        Assert.True(File.Exists(Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx")));
        Assert.Equal((ushort)3, _hyperV.StartupActions[VmId]);
    }

    [Fact]
    public async Task IdleTransfers_AreRolledBack_AndImportedOnesAreCompleted()
    {
        var idle = await OfferAsync();
        var imported = await _receiver.OfferAsync(Request() with { Mode = TransferMode.Copy, AcknowledgeWarnings = true }, UserId, CancellationToken.None);
        await SendFileAsync(imported.TransferId, "disk:0", DiskBytes);
        await SendFileAsync(imported.TransferId, DefinitionKey, DefinitionBytes);
        var copy = await _receiver.ImportAsync(imported.TransferId, CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(31));
        await _receiver.ExpireIdleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(VmFolder));
        Assert.True(Directory.Exists(imported.Plan.VmFolder));
        Assert.Equal((ushort)3, _hyperV.StartupActions[copy.VmId]);
        Assert.False(_receiver.AnyActive);
        Assert.Throws<TransferNotFoundException>(() => _receiver.Status(idle.TransferId));
    }

    [Fact]
    public async Task ActiveTransfers_AreNotExpired()
    {
        var offer = await OfferAsync();
        _time.Advance(TimeSpan.FromMinutes(29));

        await _receiver.ExpireIdleAsync(CancellationToken.None);

        Assert.Equal(IncomingTransferState.Receiving, _receiver.Status(offer.TransferId).State);
    }

    [Fact]
    public async Task AfterARestart_TicketsAreGone_AndUnfinishedTransfersAreRolledBack()
    {
        var offer = await ReceiveAllAsync();

        // A restart in the middle of the import: the planned VM is recorded, the source was never told.
        var record = _store.Find(offer.TransferId)!;
        _store.Save(record with { State = IncomingTransferState.Importing, PlannedId = VmId });
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Web", VmState.Off));
        _receiver = CreateReceiver(new IncomingTransferStore(Path.Combine(_root, "data")));

        Assert.Null(_receiver.Authenticate(offer.TransferId, offer.Ticket, Source));
        await _receiver.ExpireIdleAsync(CancellationToken.None);

        Assert.Equal(["delete-planned"], _hyperV.Log);
        Assert.Equal([VmId], _storage.DeletedVms);
        Assert.False(Directory.Exists(VmFolder));
        Assert.False(_receiver.AnyActive);
    }

    [Fact]
    public async Task AfterARestart_AnImportedTransferIsCompleted_NotDeleted()
    {
        var offer = await ReceiveAllAsync();
        await _receiver.ImportAsync(offer.TransferId, CancellationToken.None);
        _receiver = CreateReceiver(new IncomingTransferStore(Path.Combine(_root, "data")));

        await _receiver.ExpireIdleAsync(CancellationToken.None);

        Assert.Equal((ushort)3, _hyperV.StartupActions[VmId]);
        Assert.Empty(_storage.DeletedVms);
        Assert.True(File.Exists(Path.Combine(VmFolder, "Virtual Hard Disks", "Web.vhdx")));
    }

    private TransferReceiver CreateReceiver(IncomingTransferStore? store = null)
    {
        var hyperV = new FakeHyperVHost();
        var options = new LifecycleOptions { VmRootFolder = Path.Combine(_root, "vms"), HostMemoryReserveMb = 0 };
        var preflight = new TransferPreflightService(
            _inventory, new VmStorageLocation(null, options, hyperV), hyperV, new FakeHostCapacity(), new FakeDiskFiles(),
            new FakeHyperVVersions(), options);
        return new TransferReceiver(
            store ?? _store, preflight, _hyperV, _keys, _inventory, _storage, _time, NullLogger<TransferReceiver>.Instance, blockSize: Block);
    }

    private Task<IncomingTransferOffer> OfferAsync() => _receiver.OfferAsync(Request(), UserId, CancellationToken.None);

    private async Task<IncomingTransferOffer> ReceiveAllAsync()
    {
        var offer = await OfferAsync();
        await SendFileAsync(offer.TransferId, "disk:0", DiskBytes);
        await SendFileAsync(offer.TransferId, DefinitionKey, DefinitionBytes);
        return offer;
    }

    private async Task SendFileAsync(Guid id, string key, byte[] content)
    {
        for (long offset = 0; offset < content.Length; offset += Block)
        {
            await SendAsync(id, key, content, offset);
        }
    }

    private Task<IncomingFile> SendAsync(Guid id, string key, byte[] content, long offset)
    {
        var block = content.AsSpan((int)offset, (int)Math.Min(Block, content.Length - offset)).ToArray();
        return _receiver.ReceiveBlockAsync(id, key, offset, block.Length, content.Length, SHA256.HashData(block), new MemoryStream(block), CancellationToken.None);
    }

    private static IncomingTransferRequest Request() => new(Preview(), TransferMode.Move, Source, AcknowledgeWarnings: false);

    private static TransferPreview Preview() => new(
        VmId, "Web", VmState.Off, 2, "12.0", new ComputeState(2, 2048, 2048, false, false, false, 1), true,
        CimHyperVBuilder.MicrosoftWindowsTemplateId, true, false,
        [new VmAdapterDefinition(CimHyperVHost.DefaultSwitchId, "Default Switch", "00155D010203")],
        [new TransferDisk(@"D:\VMs\Web\Virtual Hard Disks\Web.vhdx", DiskBytes.Length, "scsi", 0, 0)],
        0, [], []);
}
