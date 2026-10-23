using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>Copies a file from the host into a folder in a running Windows guest.</summary>
public interface IGuestFileCopier
{
    /// <exception cref="GuestUnavailableException">The guest could not be reached.</exception>
    /// <exception cref="GuestOperationException">The copy failed in the guest.</exception>
    Task CopyAsync(Guid vmId, GuestCredential admin, string hostPath, string guestFolder, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IGuestFileCopier"/> over PowerShell Direct with Copy-Item -ToSession, as the Performance mode guest setup
/// copies driver files (seen working on Windows 11 26300, 2026-10-23).
/// </summary>
public sealed class PowerShellDirectFileCopier : IGuestFileCopier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly string EncodedScript = PowerShellDirectRunner.Encode(Script);

    public Task CopyAsync(Guid vmId, GuestCredential admin, string hostPath, string guestFolder, CancellationToken cancellationToken) =>
        PowerShellDirectRunner.RunAsync(EncodedScript, new { vmId, admin.UserName, admin.Password, hostPath, guestFolder }, Timeout, cancellationToken);

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
            Invoke-Command -Session $session -ArgumentList $request.guestFolder -ScriptBlock {
                param($folder)
                New-Item -ItemType Directory -Path $folder -Force | Out-Null
            }
            Copy-Item -Path $request.hostPath -Destination $request.guestFolder -ToSession $session -Force
            Reply @{ ok = $true; result = $null }
        }
        catch {
            Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;
}
