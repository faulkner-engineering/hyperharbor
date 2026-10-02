namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Completed pairing. Schema: PairingResult.</summary>
/// <param name="HostCertificatePem">PEM-encoded host certificate for the client to pin.</param>
/// <param name="HostProof">HMAC-SHA256 proof that the host knows the PIN, Base64 encoded on the wire.</param>
public sealed record PairingResult(Guid DeviceId, Guid HostId, string HostCertificatePem, byte[] HostProof);
