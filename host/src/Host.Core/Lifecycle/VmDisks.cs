using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>A virtual hard disk file as Hyper-V describes it.</summary>
/// <param name="MaximumSizeBytes">The size the guest sees (MaxInternalSize).</param>
/// <param name="IsVhdx">VHDX rather than VHD; only a VHDX grows while its VM runs.</param>
/// <param name="ParentPath">The parent of a differencing disk, or null.</param>
public sealed record VirtualDiskInfo(long MaximumSizeBytes, bool IsVhdx, string? ParentPath);

/// <summary>Reads and resizes virtual hard disk files through Hyper-V.</summary>
public interface IHyperVDisks
{
    /// <returns>Null when Hyper-V cannot read the file.</returns>
    Task<VirtualDiskInfo?> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Sets the disk's maximum size. Works while the VM runs for a VHDX on a SCSI controller.</summary>
    /// <param name="progress">Receives 0 to 100.</param>
    Task ResizeAsync(string path, long sizeBytes, Action<int> progress, CancellationToken cancellationToken);
}

/// <summary>What extending the guest's partition did.</summary>
/// <param name="Note">Why it was not extended, or null.</param>
public sealed record PartitionExtension(bool Extended, string? Note);

/// <summary>Extends the NTFS partition at the end of a disk that has just grown.</summary>
public interface IPartitionExtender
{
    /// <summary>Mounts the disk file of an off VM on the host, without a drive letter, and extends its last partition.</summary>
    Task<PartitionExtension> ExtendOfflineAsync(string path, CancellationToken cancellationToken);

    /// <summary>Extends the last partition of the guest disk that has <paramref name="sizeBytes"/>, over PowerShell Direct.</summary>
    /// <exception cref="GuestUnavailableException">PowerShell Direct cannot reach the guest.</exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the credential.</exception>
    Task<PartitionExtension> ExtendInGuestAsync(Guid vmId, GuestCredential admin, long sizeBytes, CancellationToken cancellationToken);
}

/// <summary><see cref="IHyperVDisks"/> through Msvm_ImageManagementService.</summary>
public sealed class CimHyperVDisks : IHyperVDisks
{
    // Msvm_VirtualHardDiskSettingData.Format.
    private const string VhdxFormat = "3";

    public Task<VirtualDiskInfo?> ReadAsync(string path, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = ImageManagementService(session);
            CimMethodResult result;
            try
            {
                result = await HyperVCim.InvokeAsync(session, service, "GetVirtualHardDiskSettingData",
                    new CimMethodParametersCollection { CimMethodParameter.Create("Path", path, CimType.String, CimFlags.In) },
                    "GetVirtualHardDiskSettingData", cancellationToken);
            }
            catch (Exception ex) when (ex is HyperVOperationException or HyperVJobFailedException)
            {
                return null;
            }

            if (result.OutParameters["SettingData"]?.Value is not string settingData)
            {
                return null;
            }

            var scalars = CimXml.ReadScalars(settingData);
            if (!scalars.TryGetValue("MaxInternalSize", out var size) || !long.TryParse(size, out var bytes))
            {
                return null;
            }

            return new VirtualDiskInfo(
                bytes,
                scalars.GetValueOrDefault("Format") == VhdxFormat,
                scalars.TryGetValue("ParentPath", out var parent) && !string.IsNullOrWhiteSpace(parent) ? parent : null);
        }, cancellationToken);

    public Task ResizeAsync(string path, long sizeBytes, Action<int> progress, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = ImageManagementService(session);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Path", path, CimType.String, CimFlags.In),
                CimMethodParameter.Create("MaxInternalSize", (ulong)sizeBytes, CimType.UInt64, CimFlags.In),
            };
            await HyperVCim.InvokeAsync(session, service, "ResizeVirtualHardDisk", parameters, "ResizeVirtualHardDisk", cancellationToken, progress);
        }, cancellationToken);

    private static CimInstance ImageManagementService(CimSession session) =>
        HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_ImageManagementService")
            ?? throw new HyperVUnavailableException("The Hyper-V image management service was not found.");
}

/// <summary>
/// <see cref="IPartitionExtender"/> with Windows PowerShell's Storage module: on the host for an off VM's disk file
/// (which needs administrator rights, so only the installed service can), or in the guest over PowerShell Direct.
/// Only a basic NTFS partition at the end of the disk is extended; anything else (a recovery partition after C:, a
/// Linux file system) is left as it is, with a note.
/// </summary>
public sealed class PowerShellPartitionExtender : IPartitionExtender
{
    private static readonly TimeSpan OfflineTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan GuestTimeout = TimeSpan.FromMinutes(3);
    private static readonly string EncodedOfflineScript = PowerShellDirectRunner.Encode(OfflineScript);
    private static readonly string EncodedGuestScript = PowerShellDirectRunner.Encode(GuestScript);

    public async Task<PartitionExtension> ExtendOfflineAsync(string path, CancellationToken cancellationToken)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            return new PartitionExtension(false,
                "The host is not running as the installed service, so it cannot mount the disk to extend the partition. Extend it in Disk Management in the VM, or grow the disk while the VM runs.");
        }

        try
        {
            return Parse(await PowerShellDirectRunner.RunAsync(EncodedOfflineScript, new { path }, OfflineTimeout, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is GuestOperationException or GuestUnavailableException)
        {
            return new PartitionExtension(false, $"The partition could not be extended: {ex.Message} Extend it in Disk Management in the VM.");
        }
    }

    public async Task<PartitionExtension> ExtendInGuestAsync(Guid vmId, GuestCredential admin, long sizeBytes, CancellationToken cancellationToken)
    {
        try
        {
            return Parse(await PowerShellDirectRunner.RunAsync(EncodedGuestScript, new { vmId, admin.UserName, admin.Password, sizeBytes }, GuestTimeout, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (GuestOperationException ex)
        {
            return new PartitionExtension(false, GuestErrors.Clean($"The partition could not be extended: {ex.Message} Extend it in Disk Management in the VM.", admin.Password));
        }
    }

    internal static PartitionExtension Parse(System.Text.Json.Nodes.JsonNode? result) =>
        new((bool?)result?["extended"] ?? false, (string?)result?["note"] is { Length: > 0 } note ? note : null);

    /// <summary>
    /// Extends the last partition of disk $number when it is basic NTFS. Shared by both scripts. When Windows RE's
    /// recovery partition follows a basic NTFS partition (Windows Setup adds one after C: when the layout has none),
    /// a running guest ($inGuest) moves it to the end of the disk the way Microsoft documents (KB5028997): Windows RE
    /// off, the recovery partition deleted, C: extended less its size, a new recovery partition made, Windows RE on.
    /// Turning Windows RE off copies it to C:, so a failure after that leaves the guest bootable, with Windows RE on C:.
    /// </summary>
    private const string ExtendFunction = """
        function MoveRecovery($number, $data, $recovery) {
            $size = $recovery.Size
            $enabled = (reagentc.exe /info | Out-String) -match 'Windows RE status:\s+Enabled'
            if ($enabled) {
                reagentc.exe /disable | Out-Null
                if ($LASTEXITCODE -ne 0) { return @{ extended = $false; note = 'Windows RE could not be turned off, so the recovery partition after C: was left in place and C: did not grow. Extend it in Disk Management in the VM.' } }
            }
            $problem = $null
            try {
                Remove-Partition -DiskNumber $number -PartitionNumber $recovery.PartitionNumber -Confirm:$false
                $max = (Get-PartitionSupportedSize -DiskNumber $number -PartitionNumber $data.PartitionNumber).SizeMax
                $target = [math]::Floor(($max - $size) / 1MB) * 1MB
                if ($target -gt $data.Size) { Resize-Partition -DiskNumber $number -PartitionNumber $data.PartitionNumber -Size $target }
                $new = New-Partition -DiskNumber $number -UseMaximumSize -GptType '{de94bba4-06d1-4d40-a16a-bfd50179d6ac}'
                $new | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'Recovery' -Confirm:$false | Out-Null
                # Required and without a drive letter, as Windows marks its own recovery partition.
                "select disk $number`nselect partition $($new.PartitionNumber)`ngpt attributes=0x8000000000000001`nexit" | diskpart.exe | Out-Null
            }
            catch { $problem = $_.Exception.Message }
            finally {
                if ($enabled) { reagentc.exe /enable | Out-Null; $reEnabled = $LASTEXITCODE -eq 0 } else { $reEnabled = $true }
            }
            $grown = (Get-Partition -DiskNumber $number -PartitionNumber $data.PartitionNumber).Size -gt $data.Size
            $notes = @()
            if ($problem) { $notes += "Moving the recovery partition stopped: $problem" }
            if (-not $reEnabled) { $notes += 'Windows RE could not be turned back on; run reagentc /enable as an administrator in the VM.' }
            return @{ extended = $grown; note = $(if ($notes.Count -gt 0) { $notes -join ' ' } else { $null }) }
        }

        function Extend($number, $inGuest) {
            $partitions = @(Get-Partition -DiskNumber $number | Sort-Object Offset)
            if ($partitions.Count -eq 0) { return @{ extended = $false; note = 'The disk has no partitions, so there was nothing to extend.' } }
            $last = $partitions[-1]
            if ($last.Type -eq 'Recovery' -and $partitions.Count -ge 2 -and $partitions[-2].Type -eq 'Basic' -and ($partitions[-2] | Get-Volume -ErrorAction SilentlyContinue).FileSystem -eq 'NTFS') {
                if (-not $inGuest) {
                    return @{ extended = $false; note = 'A recovery partition follows C:, and it can be moved only while Windows runs. Start the VM and grow the disk again by a gigabyte; HyperHarbor then moves it and extends C:.' }
                }
                return MoveRecovery $number $partitions[-2] $last
            }
            $volume = $last | Get-Volume -ErrorAction SilentlyContinue
            if ($last.Type -notin @('Basic', 'IFS') -or -not $volume -or $volume.FileSystem -ne 'NTFS') {
                return @{ extended = $false; note = 'The last partition on the disk is not a Windows (NTFS) data partition, for example a recovery partition after C:, so it was left as it is. Extend the partition in Disk Management in the VM.' }
            }
            $supported = Get-PartitionSupportedSize -DiskNumber $number -PartitionNumber $last.PartitionNumber
            if ($supported.SizeMax -gt $last.Size) {
                Resize-Partition -DiskNumber $number -PartitionNumber $last.PartitionNumber -Size $supported.SizeMax
            }
            return @{ extended = $true; note = $null }
        }

        """;

    internal const string OfflineScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 6 }

        """ + ExtendFunction + """
        try {
            $image = Mount-DiskImage -ImagePath $request.path -NoDriveLetter -PassThru
            try {
                $disk = $image | Get-Disk
                if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }
                if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }
                $result = Extend $disk.Number $false
            }
            finally { Dismount-DiskImage -ImagePath $request.path | Out-Null }
            Reply @{ ok = $true; result = $result }
        }
        catch {
            Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message }
        }
        """;

    internal const string GuestScript = """
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
            $result = Invoke-Command -Session $session -ArgumentList ([long]$request.sizeBytes) -ScriptBlock {
                param($size)
                $ErrorActionPreference = 'Stop'

        """ + ExtendFunction + """
                # The guest sees the new size once it rescans; that can take a few seconds.
                $disks = @()
                for ($attempt = 0; $attempt -lt 10 -and $disks.Count -eq 0; $attempt++) {
                    Update-HostStorageCache
                    $disks = @(Get-Disk | Where-Object { $_.Size -eq $size })
                    if ($disks.Count -eq 0) { Start-Sleep -Seconds 3 }
                }
                if ($disks.Count -ne 1) {
                    return @{ extended = $false; note = 'Windows in the VM did not show exactly one disk of the new size, so no partition was extended. Extend it in Disk Management in the VM.' }
                }
                Extend $disks[0].Number $true
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
