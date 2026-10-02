namespace HyperHarbor.Host.Core.HyperV;

/// <summary>
/// Thrown when the Hyper-V management namespace cannot be queried, for example because the
/// Hyper-V role is not enabled or the caller lacks Hyper-V Administrators rights.
/// </summary>
public sealed class HyperVUnavailableException : Exception
{
    public HyperVUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
