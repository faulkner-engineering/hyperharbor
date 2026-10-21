using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>The receiving host as the authorizing client describes it.</summary>
/// <param name="Addresses">Host names or IP addresses to try in order.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the receiving host's certificate, pinned for every request.</param>
public sealed record PeerEndpoint(Guid HostId, string Name, IReadOnlyList<string> Addresses, int Port, string Fingerprint);

/// <summary>What the source host needs to push a VM to the receiving host. ToString never includes the ticket.</summary>
/// <param name="TransferId">The receiving host's ID for the transfer, from its offer.</param>
/// <param name="Ticket">The receiving host's one-time ticket for the transfer.</param>
/// <param name="TargetGuardian">The receiving host's guardian (public certificates), required when the VM has a TPM.</param>
public sealed record TransferSendRequest(Guid VmId, TransferMode Mode, Guid TransferId, string Ticket, PeerEndpoint Target, byte[]? TargetGuardian)
{
    public override string ToString() => $"TransferSendRequest {{ VmId = {VmId}, Mode = {Mode}, TransferId = {TransferId}, Target = {Target.Name} }}";
}

/// <param name="SourceRemoved">For a move: whether this host's copy was deleted. Always false for a copy.</param>
/// <param name="Warnings">Things the user must know or do, such as deleting a copy that could not be removed.</param>
public sealed record TransferSendResult(Guid TargetVmId, string TargetName, bool SourceRemoved, IReadOnlyList<string> Warnings);

/// <summary>
/// Sends a VM to another host, as the work of a transfer job that holds the VM's lock:
/// <list type="number">
/// <item>Checks the VM again and opens its disks for reading with no write sharing, so nothing can start it meanwhile.</item>
/// <item>Exports its definition; with a TPM, the receiving host's guardian is granted only for the export.</item>
/// <item>Sends the definition files and the disks in verified blocks, resuming from the receiving host's count after a network error.</item>
/// <item>Asks the receiving host to import the VM. Until it says it did, any failure or cancel rolls its side back.</item>
/// <item>For a move, deletes this host's copy and its disks; then completes the transfer. These last steps are not canceled.</item>
/// </list>
/// </summary>
public sealed partial class TransferSender
{
    /// <summary>Network failures in a row (with no block getting through in between) before the transfer gives up.</summary>
    public const int MaxConsecutiveFailures = 5;

    public const string StagingFolderName = "transfer-staging";

    private readonly TransferPreviewService _preview;
    private readonly IHyperVTransfer _hyperV;
    private readonly KeyProtectorHandover _handover;
    private readonly VmDeletionService _deletion;
    private readonly OutgoingTransferStore _store;
    private readonly IPeerTransferChannelFactory _channels;
    private readonly string _stagingRoot;
    private readonly TimeProvider _time;
    private readonly ILogger<TransferSender> _logger;
    private readonly long _blockSize;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="dataDirectory">Exported definitions are staged in its transfer-staging folder, which only administrators can read.</param>
    /// <param name="delay">Waits between retries; Task.Delay by default.</param>
    public TransferSender(
        TransferPreviewService preview,
        IHyperVTransfer hyperV,
        KeyProtectorHandover handover,
        VmDeletionService deletion,
        OutgoingTransferStore store,
        IPeerTransferChannelFactory channels,
        string dataDirectory,
        TimeProvider time,
        ILogger<TransferSender> logger,
        long blockSize = TransferReceiver.BlockSize,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _preview = preview;
        _hyperV = hyperV;
        _handover = handover;
        _deletion = deletion;
        _store = store;
        _channels = channels;
        _stagingRoot = Path.Combine(dataDirectory, StagingFolderName);
        _time = time;
        _logger = logger;
        _blockSize = blockSize;
        _delay = delay ?? Task.Delay;
    }

    /// <param name="report">Receives each step and a percentage from 0 to 99.</param>
    /// <exception cref="TransferException">The VM cannot go, or the receiving host refused or failed.</exception>
    /// <exception cref="OperationCanceledException">The job was canceled before the receiving host imported the VM.</exception>
    public async Task<TransferSendResult> RunAsync(TransferSendRequest request, Action<string, int> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = new OutgoingTransferRecord(
            request.TransferId, request.VmId, request.VmId.ToString("D"), request.Mode, request.Target.HostId, OutgoingTransferState.Sending, _time.GetUtcNow());
        _store.Save(record);
        var staging = Path.Combine(_stagingRoot, request.TransferId.ToString("N"));
        using var channel = _channels.Open(request);
        var disks = new List<FileStream>();
        TransferPreview preview;
        TransferImportResult imported;
        try
        {
            report("Checking the virtual machine", 2);
            preview = await _preview.PreviewAsync(request.VmId, cancellationToken).ConfigureAwait(false);
            if (preview.Blockers.Count > 0)
            {
                throw new TransferException(preview.Blockers[0].Message);
            }

            record = record with { VmName = preview.Name };
            _store.Save(record);

            report("Locking the disks", 4);
            foreach (var disk in preview.Disks)
            {
                // Hyper-V opens a disk for writing to start a VM, so reading it with no write sharing keeps the VM off.
                var stream = new FileStream(disk.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
                disks.Add(stream);
                if (stream.Length != disk.SizeBytes)
                {
                    throw new TransferException($"The disk {disk.Path} changed since the transfer was checked. Start the transfer again.");
                }
            }

            report("Exporting the virtual machine's definition", 6);
            var exported = await ExportAsync(request, preview, staging, record, cancellationToken).ConfigureAwait(false);

            var files = DefinitionFiles(exported)
                .Concat(preview.Disks.Select((disk, index) => (Key: TransferReceiver.DiskKey(index), Path: disk.Path, Size: disk.SizeBytes)))
                .ToList();
            await SendFilesAsync(channel, files, disks, report, cancellationToken).ConfigureAwait(false);

            report($"Importing on {request.Target.Name}", 91);
            imported = await RetryAsync(channel.ImportAsync, cancellationToken).ConfigureAwait(false);
            record = record with { State = OutgoingTransferState.Imported };
            _store.Save(record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transfer {TransferId} of VM {VmId} to {Target} failed before the import; canceling it there.",
                request.TransferId, request.VmId, request.Target.Name);
            await CancelQuietlyAsync(channel, request).ConfigureAwait(false);
            _store.Remove(request.TransferId);
            throw;
        }
        finally
        {
            disks.ForEach(stream => stream.Dispose());
            TryDelete(staging);
        }

        // The receiving host has the VM from here on: nothing is canceled or rolled back any more.
        var warnings = new List<string>();
        var removed = false;
        if (request.Mode == TransferMode.Move)
        {
            report("Removing the virtual machine from this host", 94);
            try
            {
                await _deletion.DeleteAsync(request.VmId, new VmDeleteRequest(DeleteDisks: true, DeleteCheckpoints: false, preview.Name),
                    (step, percent) => report(step, 94 + percent * 4 / 100), CancellationToken.None).ConfigureAwait(false);
                removed = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name} was moved to {Target}, but its copy on this host could not be removed.", preview.Name, request.Target.Name);
                warnings.Add($"{preview.Name} was moved to {request.Target.Name}, but its copy on this host could not be removed ({ex.Message}). Delete it here; do not start both.");
            }
        }

        report("Completing the transfer", 98);
        try
        {
            await RetryAsync(channel.CompleteAsync, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The receiving host completes an imported transfer on its own once the ticket goes idle.
            _logger.LogWarning(ex, "Could not complete transfer {TransferId} on {Target}; it completes there on its own.", request.TransferId, request.Target.Name);
        }

        _store.Remove(request.TransferId);
        _logger.LogInformation("{Mode} of {Name} to {Target} finished as {TargetVmId}.", request.Mode, preview.Name, request.Target.Name, imported.VmId);
        return new TransferSendResult(imported.VmId, imported.Name, removed, warnings);
    }

    /// <summary>
    /// At startup: takes back a grant a restart interrupted (the original key protector was saved before the grant), and
    /// reports moves whose copy on this host was not deleted.
    /// </summary>
    public async Task RecoverAsync()
    {
        foreach (var record in _store.List())
        {
            if (record.OriginalKeyProtector is { } original && record.PeerGuardianName is { } guardian)
            {
                try
                {
                    await _handover.PutBackAsync(record.VmId, original, guardian).ConfigureAwait(false);
                    _logger.LogWarning("Took back the TPM grant that an interrupted transfer gave {Guardian} on {Name}.", guardian, record.VmName);
                }
                catch (Exception ex)
                {
                    // Kept, so the next start tries again.
                    _logger.LogError(ex, "Could not take back the TPM grant on {Name} after an interrupted transfer.", record.VmName);
                    continue;
                }
            }

            if (record.State == OutgoingTransferState.Imported && record.Mode == TransferMode.Move)
            {
                _logger.LogWarning("{Name} was moved to another host, but a restart stopped its removal here. Delete it on this host; do not start both.", record.VmName);
            }

            _store.Remove(record.TransferId);
        }

        TryDelete(_stagingRoot);
    }

    private async Task<ExportedDefinition> ExportAsync(
        TransferSendRequest request, TransferPreview preview, string staging, OutgoingTransferRecord record, CancellationToken cancellationToken)
    {
        if (!preview.TpmEnabled)
        {
            return await _hyperV.ExportAsync(request.VmId, staging, cancellationToken).ConfigureAwait(false);
        }

        if (request.TargetGuardian is not { Length: > 0 } guardian)
        {
            throw new TransferException($"{preview.Name} has a TPM, but {request.Target.Name} sent no guardian to grant it to.");
        }

        var peer = KeyProtectorHandover.PeerGuardianName(request.Target.HostId);
        var exported = await _handover.WithPeerGrantedAsync(
            request.VmId,
            guardian,
            peer,
            token => _hyperV.ExportAsync(request.VmId, staging, token),
            cancellationToken,
            original => _store.Save(record with { OriginalKeyProtector = original, PeerGuardianName = peer })).ConfigureAwait(false);

        // The original is back on the VM; nothing needs recovering any more.
        _store.Save(record);
        return exported;
    }

    /// <summary>The exported files the receiving host accepts: the VM's .vmcx, .vmgs, and .VMRS.</summary>
    private static IEnumerable<(string Key, string Path, long Size)> DefinitionFiles(ExportedDefinition exported) =>
        exported.Files
            .Where(file => DefinitionName().IsMatch(Path.GetFileName(file)))
            .Select(file => ($"definition:{Path.GetFileName(file)}", file, new FileInfo(file).Length));

    private async Task SendFilesAsync(
        IPeerTransferChannel channel,
        List<(string Key, string Path, long Size)> files,
        List<FileStream> disks,
        Action<string, int> report,
        CancellationToken cancellationToken)
    {
        var total = Math.Max(1, files.Sum(file => file.Size));
        long done = 0;
        var status = await RetryAsync(channel.StatusAsync, cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            var disk = disks.FirstOrDefault(stream => string.Equals(stream.Name, file.Path, StringComparison.OrdinalIgnoreCase));
            var owned = disk is null ? new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous) : null;
            try
            {
                var handle = (disk ?? owned)!.SafeFileHandle;
                var offset = status.Received.GetValueOrDefault(file.Key);
                done += offset;
                var failures = 0;
                while (offset < file.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    report($"Sending {Path.GetFileName(file.Path)}", 10 + (int)(done * 80 / total));
                    var length = Math.Min(_blockSize, file.Size - offset);
                    try
                    {
                        var hash = await HashAsync(handle, offset, length, cancellationToken).ConfigureAwait(false);
                        await using var block = new FileSegmentStream(handle, offset, length);
                        await channel.SendBlockAsync(file.Key, offset, length, file.Size, hash, block, cancellationToken).ConfigureAwait(false);
                        offset += length;
                        done += length;
                        failures = 0;
                    }
                    catch (TransferOffsetException ex) when (ex.Expected >= 0 && ex.Expected <= file.Size && ex.Expected != offset)
                    {
                        // The receiving host already has more (or less) of the file than this host thought: go on from there.
                        done += ex.Expected - offset;
                        offset = ex.Expected;
                    }
                    catch (PeerTransportException ex) when (++failures < MaxConsecutiveFailures)
                    {
                        _logger.LogWarning("Sending {File} failed ({Message}); retrying.", file.Key, ex.Message);
                        await _delay(Backoff(failures), cancellationToken).ConfigureAwait(false);
                        var now = await RetryAsync(channel.StatusAsync, cancellationToken).ConfigureAwait(false);
                        var resumed = now.Received.GetValueOrDefault(file.Key);
                        done += resumed - offset;
                        offset = resumed;
                    }
                }
            }
            finally
            {
                owned?.Dispose();
            }
        }
    }

    private async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        for (var failures = 1; ; failures++)
        {
            try
            {
                return await call(cancellationToken).ConfigureAwait(false);
            }
            catch (PeerTransportException ex) when (failures < MaxConsecutiveFailures)
            {
                _logger.LogWarning("A request to the other host failed ({Message}); retrying.", ex.Message);
                await _delay(Backoff(failures), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task RetryAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken) =>
        RetryAsync(async token =>
        {
            await call(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task CancelQuietlyAsync(IPeerTransferChannel channel, TransferSendRequest request)
    {
        try
        {
            await channel.CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The receiving host rolls back on its own when the ticket goes idle.
            _logger.LogWarning(ex, "Could not cancel transfer {TransferId} on {Target}; it ends there on its own.", request.TransferId, request.Target.Name);
        }
    }

    private static async Task<byte[]> HashAsync(SafeFileHandle handle, long offset, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (long position = offset, end = offset + length; position < end;)
        {
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position)), position, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new TransferException("A disk ended before its recorded size.");
            }

            hash.AppendData(buffer, 0, read);
            position += read;
        }

        return hash.GetHashAndReset();
    }

    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Pow(2, failures - 1));

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

    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\.(vmcx|vmgs|vmrs)$", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionName();
}
