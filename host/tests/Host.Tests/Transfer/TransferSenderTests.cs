using System.Security.Cryptography;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary>The sending host's job against a real <see cref="TransferReceiver"/>, both with Hyper-V faked.</summary>
public sealed class TransferSenderTests : IDisposable
{
    private const long Block = 4;
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid TargetHostId = Guid.Parse("9c8b7a65-4321-4fed-8cba-0987654321ab");
    private static readonly string SourceFingerprint = Convert.ToHexString(SHA256.HashData("source host"u8));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-sender-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _diskBytes = RandomNumberGenerator.GetBytes(10);
    private readonly string _disk;

    // The sending host.
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeDefinitions _definitions = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeDiskFiles _files = new();
    private readonly FakeHyperVTransfer _hyperV = new();
    private readonly FakeKeyProtectorAccess _keys = new();
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly OutgoingTransferStore _outgoing;
    private readonly TransferSender _sender;

    // The receiving host.
    private readonly FakeVmInventory _targetInventory = new();
    private readonly FakeHyperVTransfer _targetHyperV = new() { MoveId = VmId };
    private readonly Backup.FakeImporter _targetImporter = new();
    private readonly TransferReceiver _receiver;

    private readonly List<string> _steps = [];
    private ReceiverChannel? _channel;

    public TransferSenderTests()
    {
        _disk = Path.Combine(_root, "source", "Web.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(_disk)!);
        File.WriteAllBytes(_disk, _diskBytes);

        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Web", VmState.Off));
        _definitions.Current = () => new VmDefinition(
            VmId, "Web", 2, null, new ComputeState(2, 2048, 2048, false, false, false, 1), true, CimHyperVBuilder.MicrosoftWindowsTemplateId, true,
            [new VmAdapterDefinition(CimHyperVHost.DefaultSwitchId, "Default Switch", "00155D010203")],
            [new VmDiskDefinition(_disk, "scsi", 0, 0)], null, 128, 512);
        _storage.Attach(VmId, "Web", _disk);
        _files.Files.Add(_disk);
        _files.Sizes[_disk] = _diskBytes.Length;

        var sourceData = Path.Combine(_root, "source-data");
        _outgoing = new OutgoingTransferStore(sourceData);
        var users = new Core.Users.UserStore(sourceData);
        var owner = users.GetOrCreateDefault();
        var accounts = new Core.Provisioning.ProvisioningStore(sourceData);
        accounts.Save(new Core.Provisioning.ProvisionedAccount(VmId, owner.UserId, owner.VmAccountName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var records = new TransferRecordService(
            accounts, new Core.Provisioning.VmCredentialStore(sourceData), new Core.GameAware.GameAwareStore(sourceData), users,
            NullLogger<TransferRecordService>.Instance);
        _sender = new TransferSender(
            Preview(),
            _hyperV,
            new KeyProtectorHandover(_keys, NullLogger<KeyProtectorHandover>.Instance),
            new VmDeletionService(_inventory, _storage, _files, _jobs, NullLogger<VmDeletionService>.Instance),
            _outgoing,
            new ChannelFactory(() => _channel!),
            sourceData,
            TimeProvider.System,
            NullLogger<TransferSender>.Instance,
            Block,
            (_, _) => Task.CompletedTask,
            records);

        var hostV = new FakeHyperVHost();
        var options = new LifecycleOptions { VmRootFolder = Path.Combine(_root, "target"), HostMemoryReserveMb = 0 };
        var preflight = new TransferPreflightService(
            _targetInventory, new VmStorageLocation(null, options, hostV), hostV, new FakeHostCapacity(), new FakeDiskFiles(), new FakeHyperVVersions(), options,
            keys: new FakeKeyProtectorAccess(), tpm: new FakeTpmCapabilityProbe());
        _receiver = new TransferReceiver(
            new IncomingTransferStore(Path.Combine(_root, "target-data")), preflight, _targetHyperV, new FakeKeyProtectorAccess(), _targetInventory,
            new FakeHyperVStorage(), new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<TransferReceiver>.Instance, blockSize: Block, importer: _targetImporter);
    }

    private string TargetDisk => Path.Combine(_root, "target", "Web", "Virtual Hard Disks", "Web.vhdx");

    public void Dispose()
    {
        _jobs.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Move_SendsEverything_ThenRemovesTheSource_AndCompletes()
    {
        var request = await OfferAsync(TransferMode.Move);

        var result = await RunAsync(request);

        Assert.Equal((VmId, "Web", true), (result.TargetVmId, result.TargetName, result.SourceRemoved));
        Assert.Empty(result.Warnings);
        Assert.Equal(_diskBytes, await File.ReadAllBytesAsync(TargetDisk));
        Assert.Equal([$"import same {VmId:D}.vmcx", "fix", "validate", "realize", "startup 3"], _targetHyperV.Log);
        Assert.Equal([VmId], _storage.DeletedVms);
        Assert.Equal([_disk], _files.Deleted);
        Assert.Equal("hh-owner", Assert.Single(Assert.Single(_channel!.Records).Accounts).AccountName);
        Assert.False(_receiver.AnyActive);
        Assert.Empty(_outgoing.List());
        Assert.False(Directory.Exists(Path.Combine(_root, "source-data", TransferSender.StagingFolderName, request.TransferId.ToString("N"))));
    }

    [Fact]
    public async Task AFreshTpm_SendsOnlyTheDisks_TouchesNoKey_AndTheVmGetsANewId()
    {
        var request = await OfferAsync(TransferMode.Move, TransferTpmMode.Fresh);

        var result = await RunAsync(request);

        Assert.Equal(_targetImporter.NewId, result.TargetVmId);
        Assert.True(result.SourceRemoved);
        Assert.Empty(_keys.Log);
        Assert.Empty(_hyperV.Log);
        Assert.DoesNotContain(_targetHyperV.Log, entry => entry.StartsWith("import", StringComparison.Ordinal));
        Assert.Equal(1, _targetImporter.TpmCalls);
        Assert.Equal(_diskBytes, await File.ReadAllBytesAsync(TargetDisk));
    }

    [Fact]
    public async Task TheTpmGrant_CoversOnlyTheExport_AndTheOriginalIsSavedForRecoveryMeanwhile()
    {
        var request = await OfferAsync(TransferMode.Move);
        byte[]? savedDuringExport = null;
        byte[]? protectorDuringExport = null;
        _hyperV.OnExport = () =>
        {
            savedDuringExport = _outgoing.List().Single().OriginalKeyProtector;
            protectorDuringExport = _keys.Current;
        };

        await RunAsync(request);

        Assert.Equal(FakeKeyProtectorAccess.Original, savedDuringExport);
        Assert.NotEqual(FakeKeyProtectorAccess.Original, protectorDuringExport);
        Assert.Equal(FakeKeyProtectorAccess.Original, _keys.Current);
        Assert.Empty(_keys.Guardians);
    }

    [Fact]
    public async Task Copy_KeepsTheSource()
    {
        var request = await OfferAsync(TransferMode.Copy);

        var result = await RunAsync(request);

        Assert.False(result.SourceRemoved);
        Assert.NotEqual(VmId, result.TargetVmId);
        Assert.Empty(_storage.DeletedVms);
        Assert.Equal(_diskBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "target", "Web", "Virtual Hard Disks", "Web.vhdx")));
    }

    [Fact]
    public async Task NetworkFailures_BeforeAndAfterABlockArrives_AreResumedFromTheReceiversCount()
    {
        var request = await OfferAsync(TransferMode.Move);
        _channel!.FailBlockBeforeSending.Add(2);
        _channel.FailBlockAfterSending.Add(4);

        await RunAsync(request);

        Assert.Equal(_diskBytes, await File.ReadAllBytesAsync(TargetDisk));
        Assert.True(_channel.BlockCalls > 6);
    }

    [Fact]
    public async Task ALostImportAnswer_IsRetried_AndGetsTheSameVm()
    {
        var request = await OfferAsync(TransferMode.Move);
        _channel!.FailImportAfterSending = 1;

        var result = await RunAsync(request);

        Assert.Equal(VmId, result.TargetVmId);
        Assert.Single(_targetHyperV.Log, entry => entry.StartsWith("realize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANetworkThatStaysDown_FailsTheTransfer_AndRollsTheReceiverBack()
    {
        var request = await OfferAsync(TransferMode.Move);
        _channel!.BlocksAlwaysFail = true;

        await Assert.ThrowsAsync<PeerTransportException>(() => RunAsync(request));

        AssertRolledBack();
    }

    [Fact]
    public async Task AVmThatWasStarted_IsNotSent_AndTheReceiverIsRolledBack()
    {
        var request = await OfferAsync(TransferMode.Move);
        _inventory.SetState(VmId, VmState.Running);

        await Assert.ThrowsAsync<TransferException>(() => RunAsync(request));

        Assert.Empty(_hyperV.Log);
        AssertRolledBack();
    }

    [Fact]
    public async Task ATpmWithoutTheReceiversGuardian_IsExportedWithoutAGrant()
    {
        // The receiving host sends no guardian when it owns or already holds the key.
        var request = await OfferAsync(TransferMode.Move) with { TargetGuardian = null };

        await RunAsync(request);

        Assert.Empty(_keys.Log);
        Assert.True(File.Exists(TargetDisk));
    }

    [Fact]
    public async Task AFailedImport_RollsTheReceiverBack_AndKeepsTheSource()
    {
        var request = await OfferAsync(TransferMode.Move);
        _targetHyperV.FailOn = "realize";

        await Assert.ThrowsAsync<TransferException>(() => RunAsync(request));

        Assert.Empty(_storage.DeletedVms);
        AssertRolledBack();
    }

    [Fact]
    public async Task CancelWhileSending_RollsTheReceiverBack()
    {
        var request = await OfferAsync(TransferMode.Move);
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sender.RunAsync(request, (step, _) =>
        {
            if (step.StartsWith("Sending Web.vhdx", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        }, cancel.Token));

        Assert.Empty(_storage.DeletedVms);
        AssertRolledBack();
    }

    [Fact]
    public async Task ASourceThatCannotBeRemoved_IsAWarning_AndTheTransferStillCompletes()
    {
        var request = await OfferAsync(TransferMode.Move);
        _storage.ThrowOnDeleteVm = new IOException("in use");

        var result = await RunAsync(request);

        Assert.False(result.SourceRemoved);
        Assert.Contains("could not be removed", Assert.Single(result.Warnings), StringComparison.Ordinal);
        Assert.Contains("startup 3", _targetHyperV.Log);
        Assert.False(_receiver.AnyActive);
    }

    [Fact]
    public async Task Recover_TakesBackAGrantThatARestartInterrupted()
    {
        _keys.Current = [9, 9, 9];
        _outgoing.Save(new OutgoingTransferRecord(Guid.NewGuid(), VmId, "Web", TransferMode.Move, TargetHostId, OutgoingTransferState.Sending,
            DateTimeOffset.UtcNow, FakeKeyProtectorAccess.Original, KeyProtectorHandover.PeerGuardianName(TargetHostId)));

        await _sender.RecoverAsync();

        Assert.Equal(FakeKeyProtectorAccess.Original, _keys.Current);
        Assert.Contains("remove", _keys.Log);
        Assert.Empty(_outgoing.List());
    }

    [Fact]
    public async Task Recover_KeepsTheRecord_WhenTheGrantCannotBeTakenBack()
    {
        _keys.FailOn = "set-original";
        _outgoing.Save(new OutgoingTransferRecord(Guid.NewGuid(), VmId, "Web", TransferMode.Move, TargetHostId, OutgoingTransferState.Sending,
            DateTimeOffset.UtcNow, FakeKeyProtectorAccess.Original, KeyProtectorHandover.PeerGuardianName(TargetHostId)));

        await _sender.RecoverAsync();

        Assert.Single(_outgoing.List());
    }

    [Fact]
    public void SendRequest_HidesTheTicket()
    {
        var request = new TransferSendRequest(VmId, TransferMode.Move, Guid.NewGuid(), "secret-ticket", Target(), null);

        Assert.DoesNotContain("secret-ticket", request.ToString(), StringComparison.Ordinal);
    }

    private void AssertRolledBack()
    {
        Assert.False(_receiver.AnyActive);
        Assert.False(Directory.Exists(Path.Combine(_root, "target", "Web")));
        Assert.Equal(FakeKeyProtectorAccess.Original, _keys.Current);
        Assert.Empty(_outgoing.List());
    }

    private Task<TransferSendResult> RunAsync(TransferSendRequest request) =>
        _sender.RunAsync(request, (step, _) => _steps.Add(step), CancellationToken.None);

    /// <summary>What the authorizing client does: the source's preview, then the receiver's offer.</summary>
    private async Task<TransferSendRequest> OfferAsync(TransferMode mode, TransferTpmMode tpmMode = TransferTpmMode.Keep)
    {
        var preview = await Preview().PreviewAsync(VmId, CancellationToken.None);
        var offer = await _receiver.OfferAsync(new IncomingTransferRequest(preview, mode, SourceFingerprint, AcknowledgeWarnings: true, tpmMode), UserId, CancellationToken.None);
        _channel = new ReceiverChannel(_receiver, offer.TransferId);
        return new TransferSendRequest(VmId, mode, offer.TransferId, offer.Ticket, Target(), offer.Guardian, tpmMode);
    }

    private TransferPreviewService Preview() => new(_inventory, _definitions, _storage, _files, new FakeHyperVVersions());

    private static PeerEndpoint Target() => new(TargetHostId, "LAPTOP", ["192.0.2.10"], 48443, Convert.ToHexString(SHA256.HashData("target host"u8)));

    /// <summary>Calls the receiver directly, and can fail chosen calls the way a broken connection would.</summary>
    private sealed class ReceiverChannel(TransferReceiver receiver, Guid transferId) : IPeerTransferChannel
    {
        /// <summary>Block calls (counted from 1) that fail before reaching the receiver.</summary>
        public HashSet<int> FailBlockBeforeSending { get; } = [];

        /// <summary>Block calls (counted from 1) whose answer is lost after the receiver took the block.</summary>
        public HashSet<int> FailBlockAfterSending { get; } = [];

        public bool BlocksAlwaysFail { get; set; }

        public int FailImportAfterSending { get; set; }

        public int BlockCalls { get; private set; }

        public Task<PeerTransferStatus> StatusAsync(CancellationToken cancellationToken)
        {
            var status = receiver.Status(transferId);
            return Task.FromResult(new PeerTransferStatus(status.State, status.Files.ToDictionary(file => file.Key, file => file.Received)));
        }

        public async Task SendBlockAsync(string fileKey, long offset, long length, long fileSize, byte[] sha256, Stream block, CancellationToken cancellationToken)
        {
            var call = ++BlockCalls;
            if (BlocksAlwaysFail || FailBlockBeforeSending.Contains(call))
            {
                throw new PeerTransportException("connection reset");
            }

            await receiver.ReceiveBlockAsync(transferId, fileKey, offset, length, fileSize, sha256, block, cancellationToken);
            if (FailBlockAfterSending.Contains(call))
            {
                throw new PeerTransportException("answer lost");
            }
        }

        public async Task<TransferImportResult> ImportAsync(CancellationToken cancellationToken)
        {
            var result = await receiver.ImportAsync(transferId, cancellationToken);
            if (FailImportAfterSending-- > 0)
            {
                throw new PeerTransportException("answer lost");
            }

            return result;
        }

        public List<TransferRecords> Records { get; } = [];

        public Task SendRecordsAsync(TransferRecords records, CancellationToken cancellationToken)
        {
            Records.Add(records);
            return receiver.ReceiveRecordsAsync(transferId, records, cancellationToken);
        }

        public Task CompleteAsync(CancellationToken cancellationToken) => receiver.CompleteAsync(transferId, cancellationToken);

        public Task CancelAsync(CancellationToken cancellationToken) => receiver.CancelAsync(transferId, cancellationToken);

        public void Dispose()
        {
        }
    }
}
