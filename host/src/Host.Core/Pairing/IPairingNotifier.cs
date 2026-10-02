namespace HyperHarbor.Host.Core.Pairing;

public enum PairingOutcome
{
    Paired,
    Expired,
    TooManyAttempts,
    Cancelled,
}

/// <summary>
/// Shows pairing progress to the user at the host, normally through the tray app.
/// </summary>
public interface IPairingNotifier
{
    /// <summary>True when something is connected that can show the PIN.</summary>
    bool CanDisplayPin { get; }

    void PairingStarted(Guid pairingId, string deviceName, string pin, DateTimeOffset expiresAt);

    void PairingEnded(Guid pairingId, string deviceName, PairingOutcome outcome);
}
