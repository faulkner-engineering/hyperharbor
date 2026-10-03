namespace HyperHarbor.Host.Core.Elevation;

/// <summary>The operation needs elevation and the request has no valid elevation token.</summary>
public sealed class ElevationRequiredException : Exception
{
    public ElevationRequiredException()
        : base("This operation requires elevation. Enter the host's admin passphrase to continue.")
    {
    }
}

/// <summary>No admin passphrase is set on the host, so no device can elevate.</summary>
public sealed class ElevationUnavailableException : Exception
{
    public ElevationUnavailableException()
        : base("No admin passphrase is set on this host. Set one from the HyperHarbor tray icon on the host.")
    {
    }
}

/// <summary>The passphrase in an elevation request is wrong.</summary>
public sealed class IncorrectPassphraseException : Exception
{
    public IncorrectPassphraseException()
        : base("The admin passphrase is incorrect.")
    {
    }
}

/// <summary>Too many wrong passphrases; elevation is refused until <see cref="RetryAfter"/> passes.</summary>
public sealed class ElevationRateLimitedException : Exception
{
    public ElevationRateLimitedException(TimeSpan retryAfter)
        : base($"Too many incorrect passphrases. Try again in {Minutes(retryAfter)}.")
    {
        RetryAfter = retryAfter;
    }

    private static string Minutes(TimeSpan duration)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    public TimeSpan RetryAfter { get; }
}
