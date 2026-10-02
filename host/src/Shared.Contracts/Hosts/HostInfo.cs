namespace HyperHarbor.Shared.Contracts.Hosts;

/// <summary>Host identity and version. Schema: HostInfo.</summary>
/// <param name="CertificateFingerprint">SHA-256 fingerprint of the host TLS certificate, uppercase hex.</param>
public sealed record HostInfo(
    Guid HostId,
    string HostName,
    string Version,
    string ApiVersion,
    string CertificateFingerprint);
