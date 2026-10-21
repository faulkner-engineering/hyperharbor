using HyperHarbor.Host.Core.HostProfiles;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>What this host's Hyper-V can do with virtual TPMs, found by trying it.</summary>
/// <param name="CanEnableTpm">A new VM can get a TPM (needed for a fresh TPM).</param>
/// <param name="CanOpenGrantedKeys">A VM whose TPM key another guardian owns, and this host's guardian was granted on, starts here (needed to keep a TPM that comes from another host).</param>
/// <param name="Detail">What Windows said when something failed.</param>
public sealed record TpmCapability(bool CanEnableTpm, bool CanOpenGrantedKeys, string? Detail);

public interface ITpmCapabilityProbe
{
    /// <exception cref="TransferException">The probe itself could not run.</exception>
    Task<TpmCapability> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Tries it with a throwaway VM: a temporary guardian owns a key protector that also grants this host's guardian; the VM
/// gets that key protector and a TPM, the temporary guardian's keys are deleted, and the VM is started, which works only
/// if this host can open a key it was granted. Everything it makes is removed again. Seen on a Windows 11 build 26200 host
/// (2026-10-21): turning on a TPM failed with "client configuration settings could not be retrieved", and a granted key
/// failed with "Not implemented (0x80004001)", while the same host opened its own VMs' keys; a build 26300 host did both.
/// The answer is kept for the life of the process, since it changes only with Windows.
/// </summary>
public sealed class PowerShellTpmCapabilityProbe(ILogger<PowerShellTpmCapabilityProbe> logger) : ITpmCapabilityProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static readonly string Script = HostPowerShell.Encode("""
        $ErrorActionPreference = 'Stop'
        $null = [Console]::In.ReadToEnd()
        $name = 'HyperHarbor TPM probe ' + [guid]::NewGuid().ToString('N')
        $folder = Join-Path (Get-VMHost).VirtualMachinePath $name
        $result = [ordered]@{ canEnableTpm = $false; canOpenGrantedKeys = $false; detail = $null }
        $vm = $null
        function Clean($text) { ($text -replace '\s+', ' ').Trim() }
        try {
            $local = Get-HgsGuardian -Name 'UntrustedGuardian' -ErrorAction SilentlyContinue
            if (-not $local) { $local = New-HgsGuardian -Name 'UntrustedGuardian' -GenerateCertificates }
            $owner = New-HgsGuardian -Name $name -GenerateCertificates
            $protector = New-HgsKeyProtector -Owner $owner -Guardian $local -AllowUntrustedRoot
            $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes 32MB -NoVHD -Path $folder
            Set-VM -VM $vm -AutomaticCheckpointsEnabled $false
            Set-VMKeyProtector -VM $vm -KeyProtector $protector.RawData
            try { Enable-VMTPM -VM $vm; $result.canEnableTpm = $true }
            catch { $result.detail = 'Turning on a TPM failed: ' + (Clean $_.Exception.Message) }
            if ($result.canEnableTpm) {
                # From here only this host's guardian can open the key.
                Get-ChildItem 'Cert:\LocalMachine\Shielded VM Local Certificates' | Where-Object Subject -like "*($name)*" | Remove-Item
                Remove-HgsGuardian -Name $name | Out-Null
                try { Start-VM -VM $vm; $result.canOpenGrantedKeys = $true }
                catch { $result.detail = 'Opening a TPM key granted by another host failed: ' + (Clean $_.Exception.Message) }
            }
        }
        catch {
            if (-not $result.detail) { $result.detail = Clean $_.Exception.Message }
        }
        finally {
            if ($vm) {
                Stop-VM -VM $vm -TurnOff -Force -ErrorAction SilentlyContinue
                Remove-VM -VM $vm -Force -ErrorAction SilentlyContinue
            }
            Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
            Remove-HgsGuardian -Name $name -ErrorAction SilentlyContinue | Out-Null
            Get-ChildItem 'Cert:\LocalMachine\Shielded VM Local Certificates' | Where-Object Subject -like "*($name)*" | Remove-Item -ErrorAction SilentlyContinue
        }
        @{ ok = $true; result = $result } | ConvertTo-Json -Compress
        """);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TpmCapability? _known;

    public async Task<TpmCapability> ProbeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_known is { } known)
            {
                return known;
            }

            System.Text.Json.Nodes.JsonNode? answer;
            try
            {
                answer = await HostPowerShell.RunAsync(Script, new { }, Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (HostLeanException ex)
            {
                throw new TransferException($"This host could not check what its Hyper-V can do with virtual TPMs: {ex.Message}", ex);
            }

            _known = new TpmCapability((bool?)answer?["canEnableTpm"] == true, (bool?)answer?["canOpenGrantedKeys"] == true, (string?)answer?["detail"]);
            logger.LogInformation("Virtual TPM capability: turn on {CanEnableTpm}, open granted keys {CanOpenGrantedKeys}. {Detail}",
                _known.CanEnableTpm, _known.CanOpenGrantedKeys, _known.Detail);
            return _known;
        }
        finally
        {
            _gate.Release();
        }
    }
}
