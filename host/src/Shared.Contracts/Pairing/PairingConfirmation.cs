using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Pairing;

/// <summary>Client SPAKE2 share and confirmation. Schema: PairingConfirmation.</summary>
/// <param name="ClientShare">SPAKE2 share X, 384 bytes big-endian, Base64 encoded on the wire.</param>
/// <param name="ClientConfirmation">Confirmation cA, Base64 encoded on the wire.</param>
public sealed record PairingConfirmation(
    [property: JsonRequired] byte[] ClientShare,
    [property: JsonRequired] byte[] ClientConfirmation);
