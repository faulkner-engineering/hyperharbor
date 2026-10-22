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
/// if this host can open a key it was granted. Everything it makes is removed again, retrying while the VM is still starting
/// (a probe on a host busy receiving a transfer once left its VM running); each run also removes an earlier probe's VM. Seen on a Windows 11 build 26200 host
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
        $result = [ordered]@{ canEnableTpm = $false; canOpenGrantedKeys = $false; detail = $null; leftover = $null }
        $vm = $null
        function Clean($text) { ($text -replace '\s+', ' ').Trim() }
        # Turns the VM off and removes it, retrying for up to a minute: right after Start-VM (or on a busy host) the VM can
        # still be starting, when turning it off and removing it both fail. Returns what is left, or $null.
        function Remove-ProbeVm($id, $probeName, $probeFolder) {
            $last = $null
            for ($i = 0; $i -lt 60; $i++) {
                $current = Get-VM -Id $id -ErrorAction SilentlyContinue
                if (-not $current) { break }
                try {
                    if ($current.State -ne 'Off') { Stop-VM -VM $current -TurnOff -Force -ErrorAction Stop }
                    else { Remove-VM -VM $current -Force -ErrorAction Stop }
                }
                catch { $last = Clean $_.Exception.Message }
                Start-Sleep -Seconds 1
            }
            Remove-Item -LiteralPath $probeFolder -Recurse -Force -ErrorAction SilentlyContinue
            Remove-HgsGuardian -Name $probeName -ErrorAction SilentlyContinue | Out-Null
            Get-ChildItem 'Cert:\LocalMachine\Shielded VM Local Certificates' | Where-Object Subject -like "*($probeName)*" | Remove-Item -ErrorAction SilentlyContinue
            if (Get-VM -Id $id -ErrorAction SilentlyContinue) { return "the VM $probeName is still there ($last)" }
            if (Test-Path -LiteralPath $probeFolder) { return "the folder $probeFolder is still there" }
            return $null
        }
        try {
            # An earlier probe that could not clean up (a host stopped mid-probe, for one) is removed first. Only VMs with
            # this name prefix, which nothing but the probe uses, are touched.
            foreach ($old in @(Get-VM | Where-Object Name -like 'HyperHarbor TPM probe *')) {
                $null = Remove-ProbeVm $old.Id $old.Name (Join-Path (Get-VMHost).VirtualMachinePath $old.Name)
            }

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
                try {
                    Start-VM -VM $vm
                    $result.canOpenGrantedKeys = $true
                    for ($i = 0; $i -lt 30 -and (Get-VM -Id $vm.Id).State -ne 'Running'; $i++) { Start-Sleep -Seconds 1 }
                }
                catch { $result.detail = 'Opening a TPM key granted by another host failed: ' + (Clean $_.Exception.Message) }
            }
        }
        catch {
            if (-not $result.detail) { $result.detail = Clean $_.Exception.Message }
        }
        finally {
            if ($vm) { $result.leftover = Remove-ProbeVm $vm.Id $name $folder }
            else {
                Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
                Remove-HgsGuardian -Name $name -ErrorAction SilentlyContinue | Out-Null
                Get-ChildItem 'Cert:\LocalMachine\Shielded VM Local Certificates' | Where-Object Subject -like "*($name)*" | Remove-Item -ErrorAction SilentlyContinue
            }
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
            if ((string?)answer?["leftover"] is { Length: > 0 } leftover)
            {
                logger.LogWarning("The virtual TPM check could not clean up after itself: {Leftover}. The next check removes it.", leftover);
            }

            return _known;
        }
        finally
        {
            _gate.Release();
        }
    }
}
