namespace HyperHarbor.Shared.Contracts;

/// <summary>
/// Facts about the API contract shared by host and client.
/// </summary>
public static class ContractInfo
{
    /// <summary>Matches info.version in docs/api.yaml.</summary>
    public const string ApiVersion = "1.2.0";

    /// <summary>Base path of all API routes.</summary>
    public const string BasePath = "/api/v1";

    /// <summary>Request header that carries an elevation token (the elevation security scheme).</summary>
    public const string ElevationHeader = "X-HyperHarbor-Elevation";

    /// <summary>The "code" member of problem details, for errors a client handles differently.</summary>
    public static class ProblemCodes
    {
        public const string ElevationRequired = "elevationRequired";
        public const string ElevationUnavailable = "elevationUnavailable";
        public const string IncorrectPassphrase = "incorrectPassphrase";
        public const string TooManyAttempts = "tooManyAttempts";
        public const string ResourceWarnings = "resourceWarnings";
        public const string RequiresShutdown = "requiresShutdown";
    }
}
