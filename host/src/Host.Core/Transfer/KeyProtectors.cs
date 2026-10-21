using HyperHarbor.Host.Core.HyperV;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>
/// A VM's vTPM key protector and the guardians that can open it. On a transfer, the receiving host exports its
/// local guardian (public certificates only); the sending host grants that guardian on the VM's key protector for
/// the length of the export and then puts the original back. The receiving host can then start the VM, and the
/// TPM's contents (such as BitLocker keys) survive. No private key ever leaves either host.
/// </summary>
public interface IKeyProtectorAccess
{
    /// <summary>Receiving host: the local guardian's public certificates, as Export-HgsGuardian writes them.</summary>
    Task<byte[]> ExportLocalGuardianAsync(CancellationToken cancellationToken);

    /// <summary>The key protector as Hyper-V holds it now. Hyper-V rewrites it on a VM's first start, so never reuse bytes that were set earlier.</summary>
    Task<byte[]> GetAsync(Guid vmId, CancellationToken cancellationToken);

    Task SetAsync(Guid vmId, byte[] keyProtector, CancellationToken cancellationToken);

    /// <summary>
    /// Imports <paramref name="guardianExport"/> as a guardian named <paramref name="guardianName"/> (replacing one
    /// left by an earlier attempt) and returns <paramref name="keyProtector"/> with that guardian added.
    /// </summary>
    Task<byte[]> GrantAsync(byte[] keyProtector, byte[] guardianExport, string guardianName, CancellationToken cancellationToken);

    /// <summary>Removes the guardian entry, if it exists. Certificates are never deleted: they may belong to this host's own guardian.</summary>
    Task RemoveGuardianAsync(string guardianName, CancellationToken cancellationToken);
}

/// <summary>Grants a peer host's guardian on a VM's key protector for the length of one step, and always puts the original back.</summary>
public sealed class KeyProtectorHandover
{
    /// <summary>Largest guardian export accepted (an export is about 6 KB).</summary>
    public const int MaxGuardianExportBytes = 256 * 1024;

    private readonly IKeyProtectorAccess _access;
    private readonly ILogger<KeyProtectorHandover> _logger;

    public KeyProtectorHandover(IKeyProtectorAccess access, ILogger<KeyProtectorHandover> logger)
    {
        _access = access;
        _logger = logger;
    }

    /// <summary>The name a peer host's guardian is imported under on this host while it is granted.</summary>
    public static string PeerGuardianName(Guid peerHostId) => $"HyperHarbor peer {peerHostId:D}";

    /// <summary>
    /// Runs <paramref name="whileGranted"/> (the export) with the peer's guardian granted on the VM's key protector.
    /// Afterwards, whether it succeeded, failed, or was canceled, the original key protector is set again and
    /// checked, and the peer's guardian entry is removed.
    /// </summary>
    /// <exception cref="TransferException">The original key protector could not be put back.</exception>
    public async Task<T> WithPeerGrantedAsync<T>(
        Guid vmId,
        byte[] peerGuardianExport,
        string peerGuardianName,
        Func<CancellationToken, Task<T>> whileGranted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peerGuardianExport);
        if (peerGuardianExport.Length is 0 or > MaxGuardianExportBytes)
        {
            throw new TransferException("The other host's guardian is missing or too large.");
        }

        var original = await _access.GetAsync(vmId, cancellationToken).ConfigureAwait(false);
        if (original.Length == 0)
        {
            throw new TransferException("The VM's TPM key protector could not be read.");
        }

        Exception? failure = null;
        try
        {
            var granted = await _access.GrantAsync(original, peerGuardianExport, peerGuardianName, cancellationToken).ConfigureAwait(false);
            await _access.SetAsync(vmId, granted, cancellationToken).ConfigureAwait(false);
            return await whileGranted(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            await RestoreAsync(vmId, original, peerGuardianName, failure).ConfigureAwait(false);
        }
    }

    /// <summary>Not cancelable: a canceled transfer must still take the peer's access away.</summary>
    private async Task RestoreAsync(Guid vmId, byte[] original, string peerGuardianName, Exception? failure)
    {
        Exception? restoreFailure = null;
        try
        {
            await _access.SetAsync(vmId, original, CancellationToken.None).ConfigureAwait(false);
            var now = await _access.GetAsync(vmId, CancellationToken.None).ConfigureAwait(false);
            if (!now.AsSpan().SequenceEqual(original))
            {
                throw new TransferException("The VM's key protector differs from its original after it was put back.");
            }
        }
        catch (Exception ex)
        {
            restoreFailure = ex;
        }

        try
        {
            await _access.RemoveGuardianAsync(peerGuardianName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The guardian entry grants nothing by itself once the key protector no longer names it.
            _logger.LogWarning(ex, "Could not remove the guardian {Guardian} after a transfer.", peerGuardianName);
        }

        if (restoreFailure is not null)
        {
            _logger.LogError(restoreFailure, "Could not put the original TPM key protector back on VM {VmId}; the other host may still be able to open it.", vmId);
            if (failure is not null)
            {
                _logger.LogError(failure, "The transfer step that ran while the other host was granted failed first.");
            }

            throw new TransferException(
                "The VM's original TPM key protector could not be put back, so the other host may still be able to open this VM. Try the transfer again, or turn the VM's TPM off and on in Hyper-V Manager (BitLocker will then ask for its recovery key).",
                restoreFailure);
        }
    }
}

/// <summary><see cref="IKeyProtectorAccess"/> through Msvm_SecurityService and the HGS client classes. Needs an elevated caller.</summary>
public sealed class CimKeyProtectorAccess : IKeyProtectorAccess
{
    public Task<byte[]> ExportLocalGuardianAsync(CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            var file = TemporaryFile();
            try
            {
                using var guardian = HgsGuardians.LocalGuardian(session);
                var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("InputObject", guardian, CimType.Instance, CimFlags.In),
                    CimMethodParameter.Create("Path", file, CimType.String, CimFlags.In),
                };
                using var result = session.InvokeMethod(HgsGuardians.Namespace, "MSFT_HgsGuardian", "Export", parameters);
                return File.Exists(file)
                    ? Task.FromResult(File.ReadAllBytes(file))
                    : throw new Power.HyperVOperationException("exporting the local guardian", HgsGuardians.ReturnCode(result));
            }
            finally
            {
                File.Delete(file);
            }
        }, cancellationToken);

    public Task<byte[]> GetAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            using var security = CimVmSettings.AssociatedSingle(session, settings, "Msvm_SecuritySettingData");
            using var service = SecurityService(session);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("SecuritySettingData", CimXml.Write(security), CimType.String, CimFlags.In),
            };
            using var result = session.InvokeMethod(HyperVCim.Namespace, service, "GetKeyProtector", parameters);
            return HyperVCim.ReturnCode(result) == 0 && result.OutParameters["KeyProtector"]?.Value is byte[] keyProtector
                ? Task.FromResult(keyProtector)
                : throw new Power.HyperVOperationException("GetKeyProtector", HyperVCim.ReturnCode(result));
        }, cancellationToken);

    public Task SetAsync(Guid vmId, byte[] keyProtector, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            using var security = CimVmSettings.AssociatedSingle(session, settings, "Msvm_SecuritySettingData");
            using var service = SecurityService(session);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("KeyProtector", keyProtector, CimType.UInt8Array, CimFlags.In),
                CimMethodParameter.Create("SecuritySettingData", CimXml.Write(security), CimType.String, CimFlags.In),
            };
            using var result = await HyperVCim.InvokeAsync(session, service, "SetKeyProtector", parameters, "SetKeyProtector", cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<byte[]> GrantAsync(byte[] keyProtector, byte[] guardianExport, string guardianName, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            Remove(session, guardianName);
            var file = TemporaryFile();
            try
            {
                File.WriteAllBytes(file, guardianExport);
                var import = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("Path", file, CimType.String, CimFlags.In),
                    CimMethodParameter.Create("Name", guardianName, CimType.String, CimFlags.In),
                    CimMethodParameter.Create("AllowExpired", false, CimType.Boolean, CimFlags.In),
                    CimMethodParameter.Create("AllowUntrustedRoot", true, CimType.Boolean, CimFlags.In),
                };
                using var imported = session.InvokeMethod(HgsGuardians.Namespace, "MSFT_HgsGuardian", "Import", import);
                using var guardian = HgsGuardians.Output(imported, "importing the other host's guardian");

                var convert = new CimMethodParametersCollection { CimMethodParameter.Create("Bytes", keyProtector, CimType.UInt8Array, CimFlags.In) };
                using var converted = session.InvokeMethod(HgsGuardians.Namespace, "MSFT_HgsKeyProtector", "ConvertToByRawBytes", convert);
                using var current = HgsGuardians.Output(converted, "reading the VM's key protector");

                var grant = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("KeyProtector", current, CimType.Instance, CimFlags.In),
                    CimMethodParameter.Create("Guardian", guardian, CimType.Instance, CimFlags.In),
                    CimMethodParameter.Create("AllowUntrustedRoot", true, CimType.Boolean, CimFlags.In),
                    CimMethodParameter.Create("AllowExpired", false, CimType.Boolean, CimFlags.In),
                };
                using var granted = session.InvokeMethod(HgsGuardians.Namespace, "MSFT_HgsKeyProtector", "Grant", grant);
                using var result = HgsGuardians.Output(granted, "granting the other host's guardian");
                return result.CimInstanceProperties["RawData"]?.Value is byte[] raw
                    ? Task.FromResult(raw)
                    : throw new Power.HyperVOperationException("granting the other host's guardian (no key protector)", HgsGuardians.ReturnCode(granted));
            }
            finally
            {
                File.Delete(file);
            }
        }, cancellationToken);

    public Task RemoveGuardianAsync(string guardianName, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            Remove(session, guardianName);
            return Task.CompletedTask;
        }, cancellationToken);

    private static void Remove(CimSession session, string guardianName)
    {
        if (string.Equals(guardianName, HgsGuardians.LocalGuardianName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The host's own guardian is never removed.", nameof(guardianName));
        }

        using var existing = HgsGuardians.Find(session, guardianName);
        if (existing is null)
        {
            return;
        }

        var parameters = new CimMethodParametersCollection { CimMethodParameter.Create("Name", guardianName, CimType.String, CimFlags.In) };
        using var result = session.InvokeMethod(HgsGuardians.Namespace, "MSFT_HgsGuardian", "Remove", parameters);
    }

    private static CimInstance SecurityService(CimSession session) =>
        HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_SecurityService")
            ?? throw new HyperVUnavailableException("The Hyper-V security service was not found.");

    /// <summary>A guardian export holds public certificates only, so the temporary folder is good enough for it.</summary>
    private static string TemporaryFile() => Path.Combine(Path.GetTempPath(), $"hh-guardian-{Guid.NewGuid():N}.xml");
}
