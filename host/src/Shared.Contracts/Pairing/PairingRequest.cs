namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Begins pairing. Schema: PairingRequest.</summary>
/// <param name="ClientCertificatePem">PEM-encoded self-signed client certificate.</param>
/// <param name="ClientNonce">32 random bytes, Base64 encoded on the wire.</param>
public sealed record PairingRequest(string DeviceName, string ClientCertificatePem, byte[] ClientNonce);
