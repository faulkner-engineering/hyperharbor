namespace HyperHarbor.Host.Core.Pairing;

public enum PairingError
{
    /// <summary>The request body is invalid (name, certificate, or SPAKE2 share).</summary>
    InvalidRequest,

    /// <summary>No tray app is connected to show the PIN.</summary>
    NoDisplay,

    /// <summary>Another pairing request is pending.</summary>
    RequestPending,

    /// <summary>No pairing request has this ID.</summary>
    NotFound,

    /// <summary>The request expired or reached the attempt limit.</summary>
    Gone,

    /// <summary>The confirmation did not match.</summary>
    ConfirmationMismatch,
}

public sealed class PairingException : Exception
{
    public PairingException(PairingError error, string message)
        : base(message)
    {
        Error = error;
    }

    public PairingError Error { get; }
}
