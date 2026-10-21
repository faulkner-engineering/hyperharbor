using HyperHarbor.Host.Core.HyperV;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>Hyper-V configuration versions: a VM's own, and the ones this host can run.</summary>
public interface IHyperVVersions
{
    /// <summary>The VM's configuration version, such as "12.0", or null when Hyper-V does not report one.</summary>
    /// <exception cref="Power.VmNotFoundException">The VM does not exist.</exception>
    Task<string?> VmVersionAsync(Guid vmId, CancellationToken cancellationToken);

    /// <summary>The configuration versions this host's Hyper-V supports.</summary>
    Task<HostVersionCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken);
}

/// <summary>The configuration versions a host supports. A VM can be imported only with one of them.</summary>
public sealed record HostVersionCapabilities(IReadOnlyList<string> SupportedVersions)
{
    public bool Supports(string version) => SupportedVersions.Any(supported => SameVersion(supported, version));

    /// <summary>The newest supported version, for messages; null when none is known.</summary>
    public string? Newest => SupportedVersions
        .Select(text => (Text: text, Parsed: Version.TryParse(text, out var parsed) ? parsed : null))
        .Where(item => item.Parsed is not null)
        .OrderByDescending(item => item.Parsed)
        .Select(item => item.Text)
        .FirstOrDefault();

    /// <summary>Compares as version numbers, so "9.0" and "9.00" match; text that is not a version must match exactly.</summary>
    internal static bool SameVersion(string left, string right) =>
        Version.TryParse(left.Trim(), out var a) && Version.TryParse(right.Trim(), out var b)
            ? a.Major == b.Major && Math.Max(a.Minor, 0) == Math.Max(b.Minor, 0)
            : string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary><see cref="IHyperVVersions"/> through root\virtualization\v2.</summary>
public sealed class CimHyperVVersions : IHyperVVersions
{
    /// <summary>One settings object per supported version (verified on Windows 11: 8.0 to 12.0).</summary>
    private const string SupportedQuery =
        @"SELECT Version FROM Msvm_VirtualSystemSettingData WHERE InstanceID LIKE 'Microsoft:Definition\\VirtualSystem\\Version\\%'";

    public Task<string?> VmVersionAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            return Task.FromResult(settings.CimInstanceProperties["Version"]?.Value as string);
        }, cancellationToken);

    public Task<HostVersionCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            var versions = new List<string>();
            foreach (var instance in HyperVCim.Query(session, SupportedQuery))
            {
                using (instance)
                {
                    if (instance.CimInstanceProperties["Version"]?.Value is string { Length: > 0 } version
                        && !versions.Any(known => HostVersionCapabilities.SameVersion(known, version)))
                    {
                        versions.Add(version);
                    }
                }
            }

            return Task.FromResult(new HostVersionCapabilities(versions));
        }, cancellationToken);
}
