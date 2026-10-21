using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>A source host's proposal, as the authorizing client sends it to this (receiving) host.</summary>
/// <param name="SourceFingerprint">The SHA-256 fingerprint of the source host's certificate, which must present it on every data request.</param>
public sealed record IncomingTransferRequest(TransferPreview Preview, TransferMode Mode, string SourceFingerprint, bool AcknowledgeWarnings);

/// <summary>This host's acceptance. ToString never includes the ticket.</summary>
/// <param name="Ticket">The one-time ticket the source host sends with every data request for this transfer.</param>
/// <param name="Guardian">This host's guardian, public certificates only, when the VM has a TPM; the source grants it on the VM's key protector.</param>
public sealed record IncomingTransferOffer(Guid TransferId, string Ticket, TransferPlan Plan, byte[]? Guardian)
{
    public override string ToString() => $"IncomingTransferOffer {{ TransferId = {TransferId}, Name = {Plan.Name} }}";
}

public sealed record IncomingTransferStatus(Guid TransferId, IncomingTransferState State, IReadOnlyList<IncomingFile> Files);

/// <summary>The imported VM.</summary>
/// <param name="Warnings">What the user must know, such as an account whose User this host does not have.</param>
public sealed record TransferImportResult(Guid VmId, string Name, IReadOnlyList<string> Warnings);

/// <summary>Who an authenticated data request comes from: the transfer and the User who authorized it here.</summary>
public sealed record TransferPrincipal(Guid TransferId, Guid UserId);

/// <summary>A block arrived at the wrong offset; <see cref="Expected"/> is where the next block must start.</summary>
public sealed class TransferOffsetException(long expected)
    : Exception($"The next block of this file starts at byte {expected}.")
{
    public long Expected { get; } = expected;
}

/// <summary>No incoming transfer has this ID, or it has ended.</summary>
public sealed class TransferNotFoundException(Guid id) : Exception($"No transfer {id} is in progress on this host.");

/// <summary>
/// The receiving host's side of a transfer. The source sends each file in blocks of <see cref="BlockSize"/> bytes
/// (the last one shorter), each with its SHA-256; a block is written to the file's ".partial" copy, checked, flushed,
/// and only then counted, so a broken connection resumes at the last good block. Then the source asks for the import
/// and, once it has been told the VM was imported, completes the transfer.
/// Rules: everything is rolled back (planned VM and the VM's own folder) on cancel, expiry, failure, or restart until
/// the VM has been reported imported; after that, this host never deletes the VM.
/// </summary>
public sealed partial class TransferReceiver
{
    public const long BlockSize = 256L * 1024 * 1024;

    /// <summary>A transfer with no data request for this long is rolled back (or, once imported, completed).</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    public const long MaxDefinitionFileBytes = 1L << 30;
    public const int MaxDefinitionFiles = 8;
    public const string StagingFolderName = ".hh-transfer";
    public const string PartialSuffix = ".partial";

    private readonly IncomingTransferStore _store;
    private readonly TransferPreflightService _preflight;
    private readonly IHyperVTransfer _hyperV;
    private readonly IKeyProtectorAccess _keys;
    private readonly IVmInventory _inventory;
    private readonly IHyperVStorage _storage;
    private readonly TimeProvider _time;
    private readonly ILogger<TransferReceiver> _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly long _blockSize;
    private readonly TransferRecordService? _records;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _offers = new(1, 1);
    private readonly Dictionary<Guid, Session> _sessions = [];

    public TransferReceiver(
        IncomingTransferStore store,
        TransferPreflightService preflight,
        IHyperVTransfer hyperV,
        IKeyProtectorAccess keys,
        IVmInventory inventory,
        IHyperVStorage storage,
        TimeProvider time,
        ILogger<TransferReceiver> logger,
        TimeSpan? idleTimeout = null,
        long blockSize = BlockSize,
        TransferRecordService? records = null)
    {
        _store = store;
        _preflight = preflight;
        _hyperV = hyperV;
        _keys = keys;
        _inventory = inventory;
        _storage = storage;
        _time = time;
        _logger = logger;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        _blockSize = blockSize;
        _records = records;
    }

    /// <summary>Whether any transfer is in progress here; it keeps the host awake and holds updates.</summary>
    public bool AnyActive => _store.List().Count > 0;

    /// <summary>Checks the proposal, prepares the VM's folder, and issues the ticket.</summary>
    /// <exception cref="LifecycleValidationException">This host cannot take the VM (400 with the errors).</exception>
    /// <exception cref="ResourceWarningsException">There are warnings the user has not acknowledged.</exception>
    /// <exception cref="LifecycleConflictException">The same VM is already arriving, or the folder appeared meanwhile.</exception>
    public async Task<IncomingTransferOffer> OfferAsync(IncomingTransferRequest request, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = NormalizeFingerprint(request.SourceFingerprint)
            ?? throw new LifecycleValidationException("The source host's certificate fingerprint is not valid.",
                [new ValidationIssue("sourceFingerprint", "Use the SHA-256 fingerprint of the source host's certificate.")]);

        await _offers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.Mode == TransferMode.Move && _store.List().Any(record => record.Mode == TransferMode.Move && record.Preview.VmId == request.Preview.VmId))
            {
                throw new LifecycleConflictException($"{request.Preview.Name} is already being moved to this host.", ContractInfo.ProblemCodes.TransferInProgress);
            }

            var plan = await _preflight.CheckAsync(request.Preview, request.Mode, cancellationToken).ConfigureAwait(false);
            if (!plan.CanAccept)
            {
                throw new LifecycleValidationException(plan.Errors[0].Message, plan.Errors);
            }

            if (plan.Warnings.Count > 0 && !request.AcknowledgeWarnings)
            {
                throw new ResourceWarningsException(plan.Warnings);
            }

            if (Directory.Exists(plan.VmFolder))
            {
                throw new LifecycleConflictException($"The folder {plan.VmFolder} appeared while the transfer was being checked. Try again.");
            }

            var guardian = request.Preview.TpmEnabled ? await _keys.ExportLocalGuardianAsync(cancellationToken).ConfigureAwait(false) : null;

            var staging = Path.Combine(plan.VmFolder, StagingFolderName);
            Directory.CreateDirectory(Path.Combine(plan.VmFolder, TransferPreflight.DiskFolderName));
            Directory.CreateDirectory(Path.Combine(staging, "Virtual Machines"));
            var files = plan.Disks.Select((disk, index) => new IncomingFile(DiskKey(index), disk.TargetPath, disk.SizeBytes, 0)).ToList();
            var record = new IncomingTransferRecord(
                Guid.NewGuid(), userId, request.Mode, fingerprint, request.Preview, plan, staging, IncomingTransferState.Receiving, files, _time.GetUtcNow());
            _store.Save(record);

            var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));
            lock (_gate)
            {
                _sessions[record.Id] = new Session(Hash(ticket), _time.GetUtcNow());
            }

            _logger.LogInformation("Accepted an incoming {Mode} of {Name} as transfer {TransferId} into {Folder}.",
                request.Mode, plan.Name, record.Id, plan.VmFolder);
            return new IncomingTransferOffer(record.Id, ticket, plan, guardian);
        }
        finally
        {
            _offers.Release();
        }
    }

    /// <summary>
    /// Checks a data request: the ticket issued for this transfer and the source host's certificate. Any other ticket,
    /// certificate, or an ended transfer gives null.
    /// </summary>
    public TransferPrincipal? Authenticate(Guid transferId, string? ticket, string? clientFingerprint)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128 || NormalizeFingerprint(clientFingerprint) is not { } fingerprint)
        {
            return null;
        }

        var record = _store.Find(transferId);
        lock (_gate)
        {
            if (record is null
                || !_sessions.TryGetValue(transferId, out var session)
                || !CryptographicOperations.FixedTimeEquals(session.TicketHash, Hash(ticket))
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(fingerprint), Encoding.ASCII.GetBytes(record.SourceFingerprint))
                || _time.GetUtcNow() - session.LastActivity > _idleTimeout)
            {
                return null;
            }

            session.LastActivity = _time.GetUtcNow();
            return new TransferPrincipal(transferId, record.UserId);
        }
    }

    /// <exception cref="TransferNotFoundException">No such transfer is in progress.</exception>
    public IncomingTransferStatus Status(Guid transferId)
    {
        var record = _store.Find(transferId) ?? throw new TransferNotFoundException(transferId);
        return new IncomingTransferStatus(record.Id, record.State, record.Files);
    }

    /// <summary>Writes one block of a file: a disk from the plan ("disk:&lt;index&gt;") or an exported definition file ("definition:&lt;name&gt;").</summary>
    /// <param name="fileSize">The whole file's size; it must not change between blocks.</param>
    /// <param name="sha256">The block's SHA-256.</param>
    /// <exception cref="TransferOffsetException">The block does not start where the file's verified part ends.</exception>
    /// <exception cref="TransferException">The file, block length, or hash is wrong, or the transfer is past receiving.</exception>
    public async Task<IncomingFile> ReceiveBlockAsync(
        Guid transferId, string fileKey, long offset, long length, long fileSize, byte[] sha256, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(body);
        var session = SessionOf(transferId);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = _store.Find(transferId) ?? throw new TransferNotFoundException(transferId);
            if (record.State != IncomingTransferState.Receiving)
            {
                throw new TransferException("This transfer is no longer receiving files.");
            }

            var file = ResolveFile(record, fileKey, fileSize);
            if (offset != file.Received)
            {
                throw new TransferOffsetException(file.Received);
            }

            if (file.IsComplete)
            {
                throw new TransferException($"{file.Key} has already been received in full.");
            }

            if (sha256.Length != SHA256.HashSizeInBytes || length != Math.Min(_blockSize, file.Size - offset))
            {
                throw new TransferException($"A block of {file.Key} must be {Math.Min(_blockSize, file.Size - offset)} bytes long with a SHA-256 hash.");
            }

            await WriteBlockAsync(file.Path + PartialSuffix, offset, length, sha256, body, cancellationToken).ConfigureAwait(false);
            var received = file with { Received = offset + length };
            if (received.IsComplete)
            {
                File.Move(file.Path + PartialSuffix, file.Path);
            }

            var files = record.Files.Where(other => other.Key != file.Key).Append(received).OrderBy(other => other.Key, StringComparer.Ordinal).ToList();
            _store.Save(record with { Files = files });
            return received;
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>
    /// Keeps the VM's records until the import, which stores them under the VM's ID on this host. They stay in memory only
    /// (they can hold the VM's administrator credential), so a restart, which rolls the transfer back anyway, drops them.
    /// Sending them again replaces them.
    /// </summary>
    /// <exception cref="TransferException">The records are malformed, or the transfer is past receiving.</exception>
    public async Task ReceiveRecordsAsync(Guid transferId, TransferRecords records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Accounts.Count > TransferRecords.MaxAccounts
            || records.Accounts.Any(account => account.AccountName is not { Length: > 0 and <= 64 } || account.SshHostKey is { Length: > 256 })
            || records.AdminCredential is { UserName: not { Length: > 0 and <= 256 } } or { Password: not { Length: > 0 and <= 256 } })
        {
            throw new TransferException("The VM's records are not valid.");
        }

        var session = SessionOf(transferId);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = _store.Find(transferId) ?? throw new TransferNotFoundException(transferId);
            if (record.State != IncomingTransferState.Receiving)
            {
                throw new TransferException("The VM's records must be sent before the import.");
            }

            session.Records = records;
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Imports the received VM. On failure the planned VM is removed and the files stay, so the source can retry or cancel.</summary>
    /// <exception cref="TransferException">Files are missing or Hyper-V refused the VM.</exception>
    public async Task<TransferImportResult> ImportAsync(Guid transferId, CancellationToken cancellationToken)
    {
        var session = SessionOf(transferId);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = _store.Find(transferId) ?? throw new TransferNotFoundException(transferId);
            if (record.State == IncomingTransferState.Imported)
            {
                return new TransferImportResult(record.PlannedId!.Value, record.Plan.Name, session.ImportWarnings);
            }

            if (record.Files.FirstOrDefault(file => !file.IsComplete) is { } missing)
            {
                throw new TransferException($"{missing.Key} has not been received in full.");
            }

            var definitions = record.Files.Where(file => file.Key.EndsWith(".vmcx", StringComparison.OrdinalIgnoreCase)).ToList();
            if (definitions.Count != 1)
            {
                throw new TransferException("Exactly one VM definition (.vmcx) file must be sent before the import.");
            }

            record = record with { State = IncomingTransferState.Importing };
            _store.Save(record);

            // Cleanup must finish even when the request that asked for the import is gone.
            try
            {
                var plannedId = await _hyperV.ImportPlannedAsync(definitions[0].Path, record.Mode == TransferMode.Copy, cancellationToken).ConfigureAwait(false);
                record = record with { PlannedId = plannedId };
                _store.Save(record);
                if (record.Mode == TransferMode.Move && plannedId != record.Preview.VmId)
                {
                    throw new TransferException("The VM's definition does not belong to the VM being moved.");
                }

                var startup = await _hyperV.FixPlannedAsync(plannedId, record.Plan, cancellationToken).ConfigureAwait(false);
                record = record with { OriginalStartupAction = startup };
                _store.Save(record);
                await _hyperV.ValidatePlannedAsync(plannedId, cancellationToken).ConfigureAwait(false);
                await _hyperV.RealizePlannedAsync(plannedId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Importing transfer {TransferId} ({Name}) failed; removing the imported VM.", transferId, record.Plan.Name);
                await RemoveImportedVmAsync(record).ConfigureAwait(false);
                _store.Save(record with { State = IncomingTransferState.Receiving, PlannedId = null, OriginalStartupAction = null });
                throw ex is TransferException or OperationCanceledException ? ex : new TransferException($"Hyper-V could not import the VM: {ex.Message}", ex);
            }

            record = record with { State = IncomingTransferState.Imported };
            _store.Save(record);
            TryDelete(record.StagingFolder);
            _logger.LogInformation("Imported transfer {TransferId} as {Name} ({VmId}).", transferId, record.Plan.Name, record.PlannedId);
            session.ImportWarnings = ApplyRecords(record.PlannedId!.Value, session.Records);
            return new TransferImportResult(record.PlannedId!.Value, record.Plan.Name, session.ImportWarnings);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Ends an imported transfer: the VM gets its automatic start action back and the ticket ends.</summary>
    /// <exception cref="TransferException">The VM has not been imported yet.</exception>
    public async Task CompleteAsync(Guid transferId, CancellationToken cancellationToken)
    {
        var session = SessionOf(transferId);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = _store.Find(transferId) ?? throw new TransferNotFoundException(transferId);
            if (record.State != IncomingTransferState.Imported)
            {
                throw new TransferException("The VM has not been imported yet.");
            }

            await FinishAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>
    /// Stops a transfer. Before the import it is rolled back: the planned VM and the VM's own folder are deleted.
    /// After the import the VM is kept and the transfer is completed instead.
    /// </summary>
    /// <returns>True when the transfer was rolled back, false when the imported VM was kept.</returns>
    public async Task<bool> CancelAsync(Guid transferId, CancellationToken cancellationToken)
    {
        var session = SessionOf(transferId);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = _store.Find(transferId) ?? throw new TransferNotFoundException(transferId);
            return await EndAsync(record, "canceled").ConfigureAwait(false);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>
    /// Ends transfers with no data request for the idle timeout, and transfers whose ticket a restart lost (so run it at
    /// startup too): rolled back before the import, completed after it.
    /// </summary>
    public async Task ExpireIdleAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        foreach (var record in _store.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session? session;
            lock (_gate)
            {
                _sessions.TryGetValue(record.Id, out session);
            }

            if (session is not null && now - session.LastActivity <= _idleTimeout)
            {
                continue;
            }

            session ??= SessionOf(record.Id);
            if (!await session.Lock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                // Read again under the lock: a request may have finished it meanwhile.
                if (_store.Find(record.Id) is { } current)
                {
                    await EndAsync(current, "expired").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not end transfer {TransferId} ({Name}).", record.Id, record.Plan.Name);
            }
            finally
            {
                session.Lock.Release();
            }
        }
    }

    /// <summary>The VM exists by now and the source will be told so; records that cannot be stored are a warning, never a failure.</summary>
    private IReadOnlyList<string> ApplyRecords(Guid vmId, TransferRecords? records)
    {
        if (records is null || _records is null)
        {
            return [];
        }

        try
        {
            return _records.Apply(vmId, records);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Could not store the records of transferred VM {VmId}.", vmId);
            return ["The VM's accounts and settings could not be stored on this host. Set the VM up again on this host to connect."];
        }
    }

    private async Task<bool> EndAsync(IncomingTransferRecord record, string reason)
    {
        if (record.State == IncomingTransferState.Imported)
        {
            _logger.LogInformation("Transfer {TransferId} was {Reason} after {Name} was imported; the VM is kept.", record.Id, reason, record.Plan.Name);
            await FinishAsync(record, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        await RemoveImportedVmAsync(record).ConfigureAwait(false);
        TryDelete(record.Plan.VmFolder);
        Forget(record.Id);
        _logger.LogInformation("Transfer {TransferId} of {Name} was {Reason} and rolled back.", record.Id, record.Plan.Name, reason);
        return true;
    }

    private async Task FinishAsync(IncomingTransferRecord record, CancellationToken cancellationToken)
    {
        if (record.OriginalStartupAction is { } startup && record.PlannedId is { } vmId)
        {
            await _hyperV.SetAutomaticStartupActionAsync(vmId, startup, cancellationToken).ConfigureAwait(false);
        }

        TryDelete(record.StagingFolder);
        Forget(record.Id);
        _logger.LogInformation("Transfer {TransferId} of {Name} is complete.", record.Id, record.Plan.Name);
    }

    /// <summary>Removes a planned or realized VM this transfer created; only ever called before the source has been told it was imported.</summary>
    private async Task RemoveImportedVmAsync(IncomingTransferRecord record)
    {
        if (record.PlannedId is not { } vmId)
        {
            return;
        }

        try
        {
            await _hyperV.DeletePlannedAsync(vmId, CancellationToken.None).ConfigureAwait(false);
            if (await _inventory.GetAsync(vmId, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                await _storage.DeleteVmAsync(vmId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove the VM {VmId} that transfer {TransferId} imported; remove it in Hyper-V Manager.", vmId, record.Id);
        }
    }

    private void Forget(Guid transferId)
    {
        _store.Remove(transferId);
        lock (_gate)
        {
            _sessions.Remove(transferId);
        }
    }

    private Session SessionOf(Guid transferId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(transferId, out var session))
            {
                return session;
            }

            // A record without a session lost its ticket in a restart; it can only be ended (ExpireIdleAsync).
            if (_store.Find(transferId) is null)
            {
                throw new TransferNotFoundException(transferId);
            }

            session = new Session([], DateTimeOffset.MinValue);
            _sessions[transferId] = session;
            return session;
        }
    }

    private static IncomingFile ResolveFile(IncomingTransferRecord record, string key, long size)
    {
        if (record.Files.FirstOrDefault(file => file.Key == key) is { } known)
        {
            return known.Size == size ? known : throw new TransferException($"{key} is {known.Size} bytes, not {size}; it changed since the transfer started.");
        }

        if (!key.StartsWith("definition:", StringComparison.Ordinal) || !DefinitionName().IsMatch(key["definition:".Length..]))
        {
            throw new TransferException($"{key} is not a file of this transfer.");
        }

        if (size is <= 0 or > MaxDefinitionFileBytes ||record.Files.Count(file => file.Key.StartsWith("definition:", StringComparison.Ordinal)) >= MaxDefinitionFiles)
        {
            throw new TransferException($"{key} is too large, or too many definition files were sent.");
        }

        return new IncomingFile(key, Path.Combine(record.StagingFolder, "Virtual Machines", key["definition:".Length..]), size, 0);
    }

    private static async Task WriteBlockAsync(string partial, long offset, long length, byte[] sha256, Stream body, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous);

        // Anything past the verified part is from a block that never finished.
        stream.SetLength(offset);
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long written = 0;
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (written + read > length)
                {
                    throw new TransferException($"The block is longer than {length} bytes.");
                }

                hash.AppendData(buffer, 0, read);
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }

            if (written != length)
            {
                throw new TransferException($"The block ended after {written} of {length} bytes.");
            }

            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), sha256))
            {
                throw new TransferException("The block's SHA-256 does not match; send it again.");
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            stream.SetLength(offset);
            throw;
        }
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Folder} after a transfer.", folder);
        }
    }

    public static string DiskKey(int index) => $"disk:{index}";

    /// <summary>Upper-case hex of a SHA-256 fingerprint (colons and spaces allowed in the input), or null when it is not one.</summary>
    internal static string? NormalizeFingerprint(string? fingerprint)
    {
        var hex = fingerprint?.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return hex is { Length: 64 } && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static byte[] Hash(string ticket) => SHA256.HashData(Encoding.UTF8.GetBytes(ticket));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The files Hyper-V's export writes: &lt;VM ID&gt;.vmcx, .vmgs, and .VMRS.</summary>
    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\.(vmcx|vmgs|vmrs)$", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionName();

    /// <summary>The ticket's hash and when it was last used; one data operation at a time per transfer.</summary>
    private sealed class Session(byte[] ticketHash, DateTimeOffset lastActivity)
    {
        public byte[] TicketHash { get; } = ticketHash;

        public DateTimeOffset LastActivity { get; set; } = lastActivity;

        public SemaphoreSlim Lock { get; } = new(1, 1);

        /// <summary>The VM's records until the import stores them. In memory only: they can hold a credential.</summary>
        public TransferRecords? Records { get; set; }

        /// <summary>What storing the records reported, for a repeated import request.</summary>
        public IReadOnlyList<string> ImportWarnings { get; set; } = [];
    }
}
