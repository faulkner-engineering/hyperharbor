namespace HyperHarbor.Shared.Contracts;

/// <summary>
/// Facts about the API contract shared by host and client.
/// </summary>
public static class ContractInfo
{
    /// <summary>Matches info.version in docs/api.yaml.</summary>
    public const string ApiVersion = "1.12.0";

    /// <summary>Base path of all API routes.</summary>
    public const string BasePath = "/api/v1";

    /// <summary>Request header that carries an elevation token (the elevation security scheme).</summary>
    public const string ElevationHeader = "X-HyperHarbor-Elevation";

    /// <summary>Request header that carries a console ticket when opening a console tunnel.</summary>
    public const string ConsoleTicketHeader = "X-HyperHarbor-Console-Ticket";

    /// <summary>The Upgrade protocol of a console tunnel: raw bytes to the VM's console after 101.</summary>
    public const string ConsoleUpgradeProtocol = "hyperharbor-console";

    /// <summary>The "code" member of problem details, for errors a client handles differently.</summary>
    public static class ProblemCodes
    {
        public const string ElevationRequired = "elevationRequired";
        public const string ElevationUnavailable = "elevationUnavailable";
        public const string IncorrectPassphrase = "incorrectPassphrase";
        public const string TooManyAttempts = "tooManyAttempts";
        public const string ResourceWarnings = "resourceWarnings";
        public const string RequiresShutdown = "requiresShutdown";
        public const string ConsoleSetupRequired = "consoleSetupRequired";
        public const string VmNotRunning = "vmNotRunning";
        public const string ConsolePasswordPolicy = "consolePasswordPolicy";
        public const string VmMustBeOff = "vmMustBeOff";
        public const string GpuUnavailable = "gpuUnavailable";
        public const string CredentialRequired = "credentialRequired";

        /// <summary>503: the host is installing an update; retry after Retry-After.</summary>
        public const string Updating = "updating";

        /// <summary>409: the host runs without being installed, so it does not update itself.</summary>
        public const string UpdatesUnsupported = "updatesUnsupported";

        /// <summary>409: the host's Windows edition cannot accept Remote Desktop connections (Home).</summary>
        public const string RemoteDesktopUnsupported = "remoteDesktopUnsupported";

        /// <summary>409: the host process lacks administrator rights for this change; the installed service has them.</summary>
        public const string RequiresInstalledService = "requiresInstalledService";

        /// <summary>409: no update has been downloaded and tested yet, so there is nothing to install.</summary>
        public const string UpdateNotReady = "updateNotReady";
    }
}
