namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Pending pairing request. Schema: PairingRequestCreated.</summary>
/// <param name="HostNonce">32 random bytes, Base64 encoded on the wire.</param>
public sealed record PairingRequestCreated(Guid PairingId, byte[] HostNonce, DateTimeOffset ExpiresAt);
