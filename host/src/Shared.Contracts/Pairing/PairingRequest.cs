using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Begins pairing. Schema: PairingRequest.</summary>
/// <param name="ClientCertificatePem">PEM-encoded self-signed client certificate.</param>
/// <param name="ClientNonce">32 random bytes, Base64 encoded on the wire.</param>
public sealed record PairingRequest(
    [property: JsonRequired] string DeviceName,
    [property: JsonRequired] string ClientCertificatePem,
    [property: JsonRequired] byte[] ClientNonce);
