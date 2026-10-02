namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Completed pairing. Schema: PairingResult.</summary>
/// <param name="HostCertificatePem">PEM-encoded host certificate for the client to pin.</param>
/// <param name="HostConfirmation">Confirmation cB, Base64 encoded on the wire.</param>
/// <param name="UserId">The User the new device belongs to.</param>
public sealed record PairingResult(Guid DeviceId, Guid UserId, Guid HostId, string HostCertificatePem, byte[] HostConfirmation);
