using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Core.Performance;

/// <param name="RebootRequired">Some files were in use in the guest; they are replaced when it restarts.</param>
public sealed record GuestSetupResult(bool RebootRequired);

/// <summary>Copies a host GPU driver into a running Windows guest and writes registry values there.</summary>
public interface IGuestPerformanceSetup
{
    /// <param name="registry">Values to write; null writes none (a driver re-sync).</param>
    Task<GuestSetupResult> RunAsync(Guid vmId, GuestCredential admin, GpuDriverPackage driver, IReadOnlyList<GuestRegistryValue>? registry, CancellationToken cancellationToken);
}

/// <summary>
/// Guest setup over PowerShell Direct, through <see cref="PowerShellDirectRunner"/>. Driver store folders are
/// copied with Copy-Item -ToSession into the guest's System32\HostDriverStore\FileRepository, where the
/// paravirtual GPU driver loads them. Other driver files are copied to a staging folder in the guest and
/// then into place; a file in use is replaced at the next restart with MoveFileEx(DELAY_UNTIL_REBOOT).
/// </summary>
public sealed class PowerShellDirectPerformanceSetup : IGuestPerformanceSetup
{
    /// <summary>Driver packages are hundreds of megabytes to over a gigabyte.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private static readonly string EncodedScript = PowerShellDirectRunner.Encode(Script);

    public async Task<GuestSetupResult> RunAsync(Guid vmId, GuestCredential admin, GpuDriverPackage driver, IReadOnlyList<GuestRegistryValue>? registry, CancellationToken cancellationToken)
    {
        var result = await PowerShellDirectRunner.RunAsync(EncodedScript, new
        {
            vmId,
            admin.UserName,
            admin.Password,
            driverStoreFolders = driver.DriverStoreFolders,
            windowsFiles = driver.WindowsFiles,
            windowsFolders = driver.WindowsFolders,
            registry = registry?.Select(value => new { key = value.Key, name = value.Name, value = value.Value }),
        }, Timeout, cancellationToken).ConfigureAwait(false);
        return new GuestSetupResult((bool?)result?["rebootRequired"] ?? false);
    }

    /// <summary>
    /// Host-side script. Reads one JSON request from stdin and writes one JSON line, as
    /// <see cref="PowerShellDirectRunner"/> expects. Paths in the request are host paths (driver store folders)
    /// or paths relative to the Windows folder (files and folders under System32 and SysWOW64).
    /// </summary>
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 4 }

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
            $windows = Invoke-Command -Session $session -ScriptBlock { $env:SystemRoot }
            $makeFolder = { param($path) New-Item -ItemType Directory -Force -Path $path | Out-Null }

            $repository = Join-Path $windows 'System32\HostDriverStore\FileRepository'
            Invoke-Command -Session $session -ScriptBlock $makeFolder -ArgumentList $repository
            foreach ($folder in @($request.driverStoreFolders)) {
                Copy-Item -Path $folder -Destination $repository -ToSession $session -Recurse -Force
            }

            foreach ($relative in @($request.windowsFolders)) {
                $source = Join-Path $env:SystemRoot $relative
                if (Test-Path $source) {
                    $parent = Split-Path (Join-Path $windows $relative)
                    Invoke-Command -Session $session -ScriptBlock $makeFolder -ArgumentList $parent
                    Copy-Item -Path $source -Destination $parent -ToSession $session -Recurse -Force
                }
            }

            $staging = Join-Path $windows 'Temp\HyperHarbor\gpu'
            Invoke-Command -Session $session -ScriptBlock $makeFolder -ArgumentList $staging
            $staged = @()
            foreach ($relative in @($request.windowsFiles)) {
                $source = Join-Path $env:SystemRoot $relative
                if (-not (Test-Path $source)) { continue }
                $folder = Join-Path $staging (Split-Path $relative)
                Invoke-Command -Session $session -ScriptBlock $makeFolder -ArgumentList $folder
                Copy-Item -Path $source -Destination $folder -ToSession $session -Force
                $staged += $relative
            }

            $reboot = Invoke-Command -Session $session -ArgumentList $staging, $windows, $staged -ScriptBlock {
                param($staging, $windows, $files)
                Add-Type -Namespace HyperHarbor -Name Native -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool MoveFileEx(string from, string to, int flags);'
                $reboot = $false
                foreach ($relative in @($files)) {
                    $from = Join-Path $staging $relative
                    $to = Join-Path $windows $relative
                    try { Copy-Item -Path $from -Destination $to -Force -ErrorAction Stop }
                    catch {
                        # MOVEFILE_REPLACE_EXISTING | MOVEFILE_DELAY_UNTIL_REBOOT: replaced when the guest restarts.
                        if (-not [HyperHarbor.Native]::MoveFileEx($from, $to, 5)) { throw "Could not schedule $relative to be replaced at restart." }
                        $reboot = $true
                    }
                }
                $reboot
            }

            if ($request.registry) {
                Invoke-Command -Session $session -ArgumentList (, @($request.registry)) -ScriptBlock {
                    param($values)
                    foreach ($value in $values) {
                        if (-not (Test-Path $value.key)) { New-Item -Path $value.key -Force | Out-Null }
                        New-ItemProperty -Path $value.key -Name $value.name -Value ([int]$value.value) -PropertyType DWord -Force | Out-Null
                    }
                }
            }

            Reply @{ ok = $true; result = @{ rebootRequired = [bool]$reboot } }
        }
        catch {
            Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;
}
