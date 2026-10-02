namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Pending pairing request. Schema: PairingRequestCreated.</summary>
/// <param name="HostShare">SPAKE2 share Y, 384 bytes big-endian, Base64 encoded on the wire.</param>
public sealed record PairingRequestCreated(Guid PairingId, byte[] HostShare, DateTimeOffset ExpiresAt);
