using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>What the guest reported: its volumes, and the volumes whose decryption could not be started.</summary>
public sealed record GuestEncryptionReport(IReadOnlyList<GuestVolumeEncryption> Volumes, IReadOnlyList<string> Problems);

/// <summary>BitLocker in a running Windows guest, over PowerShell Direct with its stored administrator.</summary>
public interface IGuestDiskEncryption
{
    /// <exception cref="GuestUnavailableException">PowerShell Direct cannot reach the guest.</exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the credential.</exception>
    /// <exception cref="GuestOperationException">The guest could not read its volumes.</exception>
    Task<GuestEncryptionReport> ReadAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken);

    /// <summary>Starts decrypting every encrypted volume, data volumes first, and returns the volumes afterwards.</summary>
    /// <exception cref="GuestUnavailableException">PowerShell Direct cannot reach the guest.</exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the credential.</exception>
    /// <exception cref="GuestOperationException">The guest could not read its volumes.</exception>
    Task<GuestEncryptionReport> TurnOffAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken);
}

/// <summary>
/// Reads BitLocker's state in a Windows guest and turns it off, as a turnOffDiskEncryption job. Decryption runs in
/// the guest in the background (also across restarts), so the job ends once it has started; the state shows progress.
/// Encrypted guest disks make every backup larger (see VhdxEncryptionProbe).
/// </summary>
public sealed class VmDiskEncryptionService(
    IVmInventory inventory,
    VmCredentialStore credentials,
    IGuestDiskEncryption guest,
    VmJobStore jobs,
    ILogger<VmDiskEncryptionService> logger)
{
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="LifecycleConflictException">The VM is not running, not set up, or not Windows.</exception>
    public async Task<VmDiskEncryption> ReadAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var (vm, admin) = await RequireAsync(vmId, "read its disk encryption", cancellationToken).ConfigureAwait(false);
        var report = await CleanAsync(() => guest.ReadAsync(vm.Id, admin, cancellationToken), admin).ConfigureAwait(false);
        return new VmDiskEncryption(vm.Id, report.Volumes, []);
    }

    /// <summary>Starts a turnOffDiskEncryption job.</summary>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="LifecycleConflictException">The VM is not running, not set up, or not Windows.</exception>
    /// <exception cref="VmBusyException">Another operation holds the VM.</exception>
    public async Task<VmJobSnapshot> TurnOffAsync(Guid vmId, Guid userId, Action<VmJobSnapshot>? onFinished, CancellationToken cancellationToken)
    {
        var (vm, admin) = await RequireAsync(vmId, "turn off its disk encryption", cancellationToken).ConfigureAwait(false);
        return jobs.Start(VmJobKind.TurnOffDiskEncryption, vmId, userId, "Turning off disk encryption", async context =>
        {
            var report = await CleanAsync(() => guest.TurnOffAsync(vm.Id, admin, context.Stopping), admin).ConfigureAwait(false);
            var problems = report.Problems.Select(problem => GuestErrors.Clean(problem, admin.Password)).ToList();
            if (problems.Count > 0 && !report.Volumes.Any(v => v.State == GuestEncryptionState.Decrypting))
            {
                throw new GuestOperationException($"{vm.Name} could not start decrypting: {string.Join(" ", problems)}");
            }

            logger.LogInformation(
                "Started decrypting {Count} volumes of {Name} ({VmId}).", report.Volumes.Count(v => v.State == GuestEncryptionState.Decrypting), vm.Name, vm.Id);
            context.ReportDiskEncryption(new VmDiskEncryption(vm.Id, report.Volumes, problems));
        }, onFinished);
    }

    private async Task<(Vm Vm, GuestCredential Admin)> RequireAsync(Guid vmId, string action, CancellationToken cancellationToken)
    {
        var vm = await inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        if (vm.GuestOs?.Family == GuestOsFamily.Linux)
        {
            throw new LifecycleConflictException($"{vm.Name} runs Linux, which does not use BitLocker. A LUKS volume is turned off in the guest itself.");
        }

        if (vm.State != VmState.Running)
        {
            throw new LifecycleConflictException($"{vm.Name} is not running. Start it, then {action}.", ContractInfo.ProblemCodes.VmNotRunning);
        }

        var admin = credentials.Find(vmId)
            ?? throw new LifecycleConflictException(
                $"{vm.Name} has no stored administrator credential, so HyperHarbor cannot {action}. Turn BitLocker off in Windows Settings in the VM.",
                ContractInfo.ProblemCodes.VmNotProvisioned);
        return (vm, admin);
    }

    /// <summary>Guest errors can echo what the script was given, so the password is removed from them.</summary>
    private static async Task<GuestEncryptionReport> CleanAsync(Func<Task<GuestEncryptionReport>> call, GuestCredential admin)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (GuestOperationException ex)
        {
            throw new GuestOperationException(GuestErrors.Clean(ex.Message, admin.Password));
        }
    }
}

/// <summary>
/// Win32_EncryptableVolume in the guest, which every Windows edition has (Home uses it for device encryption), unlike
/// the BitLocker PowerShell module.
/// </summary>
public sealed class PowerShellGuestDiskEncryption : IGuestDiskEncryption
{
    private static readonly TimeSpan GuestTimeout = TimeSpan.FromMinutes(3);
    private static readonly string EncodedScript = PowerShellDirectRunner.Encode(Script);

    public Task<GuestEncryptionReport> ReadAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken) =>
        RunAsync(vmId, admin, turnOff: false, cancellationToken);

    public Task<GuestEncryptionReport> TurnOffAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken) =>
        RunAsync(vmId, admin, turnOff: true, cancellationToken);

    private static async Task<GuestEncryptionReport> RunAsync(Guid vmId, GuestCredential admin, bool turnOff, CancellationToken cancellationToken) =>
        Parse(await PowerShellDirectRunner.RunAsync(EncodedScript, new { vmId, admin.UserName, admin.Password, turnOff }, GuestTimeout, cancellationToken)
            .ConfigureAwait(false));

    /// <summary>The script's result: the volumes with Win32_EncryptableVolume's ConversionStatus, and the problems.</summary>
    internal static GuestEncryptionReport Parse(JsonNode? result)
    {
        var volumes = (result?["volumes"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(volume => new GuestVolumeEncryption(
                (string?)volume["mountPoint"] ?? "",
                (bool?)volume["os"] ?? false,
                ((int?)volume["status"] ?? 0) switch
                {
                    1 => GuestEncryptionState.Encrypted,
                    2 => GuestEncryptionState.Encrypting,
                    3 => GuestEncryptionState.Decrypting,
                    4 or 5 => GuestEncryptionState.Paused,
                    _ => GuestEncryptionState.Decrypted,
                },
                Math.Clamp((int?)volume["percent"] ?? 0, 0, 100)))
            .ToList();
        var problems = (result?["problems"] as JsonArray ?? []).Select(problem => (string?)problem).OfType<string>().ToList();
        return new GuestEncryptionReport(volumes, problems);
    }

    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 6 }

        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
        }
        catch {
            Reply @{ ok = $false; stage = 'connect'; error = $_.Exception.Message }
            exit 0
        }

        try {
            $result = Invoke-Command -Session $session -ArgumentList ([bool]$request.turnOff) -ScriptBlock {
                param($turnOff)
                $ErrorActionPreference = 'Stop'
                # Fixed volumes only (0 is the Windows volume, 1 a data volume); removable drives are left alone.
                $volumes = @(Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume |
                    Where-Object { $_.VolumeType -in 0, 1 })
                function Name($volume) { if ($volume.DriveLetter) { $volume.DriveLetter } else { $volume.DeviceID } }
                $problems = @()
                if ($turnOff) {
                    # Data volumes first: Windows refuses to decrypt its own volume while it holds a data volume's
                    # automatic unlock key.
                    foreach ($volume in ($volumes | Sort-Object { $_.VolumeType -eq 0 })) {
                        $status = Invoke-CimMethod -InputObject $volume -MethodName GetConversionStatus
                        # 1 encrypted, 2 encrypting, 4 encryption paused.
                        if ($status.ConversionStatus -in 1, 2, 4) {
                            $answer = Invoke-CimMethod -InputObject $volume -MethodName Decrypt
                            if ($answer.ReturnValue -ne 0) {
                                $problems += ('{0} could not start decrypting (error 0x{1:X8}).' -f (Name $volume), $answer.ReturnValue)
                            }
                        }
                    }
                }

                $list = @(foreach ($volume in $volumes) {
                    $status = Invoke-CimMethod -InputObject $volume -MethodName GetConversionStatus
                    @{ mountPoint = (Name $volume); os = ($volume.VolumeType -eq 0); status = [int]$status.ConversionStatus; percent = [int]$status.EncryptionPercentage }
                })
                @{ volumes = $list; problems = $problems }
            }
            Reply @{ ok = $true; result = $result }
        }
        catch {
            Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;
}
