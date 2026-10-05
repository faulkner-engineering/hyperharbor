using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>A provisioned package as the guest reports it.</summary>
public sealed record GuestAppxPackage(string Name, string Version, string PublisherId);

/// <summary>The guest's Windows build and edition, and its provisioned packages.</summary>
/// <param name="CurrentBuild">The major build, for example 26100.</param>
/// <param name="Ubr">The update build revision, for example 2033.</param>
/// <param name="Edition">EditionID, for example Professional.</param>
public sealed record GuestAppxInventory(string CurrentBuild, int Ubr, string Edition, IReadOnlyList<GuestAppxPackage> Packages)
{
    public string FullBuild => $"10.0.{CurrentBuild}.{Ubr}";
}

/// <summary>Reads what is set up in a running Windows guest, for setup profiles. Read-only.</summary>
public interface IGuestProfileReader
{
    Task<GuestAppxInventory> ReadAppxAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IGuestProfileReader"/> over PowerShell Direct, as the VM's stored administrator. Each read has its
/// own script, to stay under the command line limit (see <see cref="PowerShellDirectRunner"/>).
/// </summary>
public sealed class PowerShellDirectProfileReader : IGuestProfileReader
{
    private static readonly TimeSpan AppxTimeout = TimeSpan.FromMinutes(2);
    private static readonly string EncodedAppxScript = PowerShellDirectRunner.Encode(AppxScript);

    public async Task<GuestAppxInventory> ReadAppxAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken)
    {
        var result = await PowerShellDirectRunner.RunAsync(EncodedAppxScript, new { vmId, admin.UserName, admin.Password }, AppxTimeout, cancellationToken).ConfigureAwait(false);
        return ParseAppx(result);
    }

    /// <exception cref="GuestOperationException">The result does not have the expected shape.</exception>
    internal static GuestAppxInventory ParseAppx(JsonNode? result)
    {
        if (result is not JsonObject inventory || (string?)inventory["build"] is not { Length: > 0 } build)
        {
            throw new GuestOperationException("The guest's package list did not have the expected shape.");
        }

        var packages = inventory["packages"] switch
        {
            JsonArray array => array,
            JsonObject single => [single.DeepClone()],
            _ => [],
        };
        return new GuestAppxInventory(
            build,
            (int?)inventory["ubr"] ?? 0,
            (string?)inventory["edition"] ?? "Unknown",
            packages.OfType<JsonObject>()
                .Where(package => (string?)package["name"] is { Length: > 0 })
                .Select(package => new GuestAppxPackage((string)package["name"]!, (string?)package["version"] ?? "", (string?)package["publisherId"] ?? ""))
                .ToList());
    }

    /// <summary>
    /// Host-side script: provisioned packages (Get-AppxProvisionedPackage -Online) with the Windows build and
    /// edition. Output is UTF-8, because package and publisher names may not be ASCII.
    /// </summary>
    internal const string AppxScript = """
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
            $result = Invoke-Command -Session $session -ScriptBlock {
                $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
                $packages = @(Get-AppxProvisionedPackage -Online | ForEach-Object {
                    @{ name = [string]$_.DisplayName; version = [string]$_.Version; publisherId = [string]$_.PublisherId }
                })
                @{ build = [string]$version.CurrentBuild; ubr = [int]$version.UBR; edition = [string]$version.EditionID; packages = $packages }
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
