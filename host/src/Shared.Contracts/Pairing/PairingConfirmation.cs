using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Client proof of PIN knowledge. Schema: PairingConfirmation.</summary>
/// <param name="Proof">HMAC-SHA256 proof, Base64 encoded on the wire.</param>
public sealed record PairingConfirmation([property: JsonRequired] byte[] Proof);
