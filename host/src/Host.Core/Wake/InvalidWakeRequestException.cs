namespace HyperHarbor.Host.Core.Wake;

/// <summary>A wake request has invalid parameters (unknown check IDs or an out-of-range delay).</summary>
public sealed class InvalidWakeRequestException : Exception
{
    public InvalidWakeRequestException(string message)
        : base(message)
    {
    }
}
