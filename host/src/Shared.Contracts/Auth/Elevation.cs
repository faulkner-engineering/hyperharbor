using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Auth;

/// <summary>Asks for elevation with the host's admin passphrase. Schema: ElevateRequest.</summary>
/// <param name="Passphrase">Never logged or stored by the host.</param>
public sealed record ElevateRequest([property: JsonRequired] string Passphrase)
{
    /// <summary>Keeps the passphrase out of logs and exception messages.</summary>
    public override string ToString() => "ElevateRequest { Passphrase = [hidden] }";
}

/// <summary>A short-lived elevation token for the calling device. Schema: ElevationGrant.</summary>
/// <param name="Token">Sent back in the <see cref="ContractInfo.ElevationHeader"/> header. Never logged.</param>
public sealed record ElevationGrant(string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>Keeps the token out of logs and exception messages.</summary>
    public override string ToString() => $"ElevationGrant {{ Token = [hidden], ExpiresAt = {ExpiresAt:u} }}";
}

/// <summary>Elevation state of the calling device. Schema: ElevationStatus.</summary>
/// <param name="Configured">True when an admin passphrase has been set in the host tray.</param>
/// <param name="Active">True when the calling device holds an unexpired elevation token.</param>
/// <param name="ExpiresAt">When the active token expires, or null when none is active.</param>
public sealed record ElevationStatus(
    bool Configured,
    bool Active,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ExpiresAt);
