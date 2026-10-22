namespace HyperHarbor.Shared.Contracts;

/// <summary>
/// Facts about the API contract shared by host and client.
/// </summary>
public static class ContractInfo
{
    /// <summary>Matches info.version in docs/api.yaml.</summary>
    public const string ApiVersion = "1.25.0";

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

        /// <summary>409: package search needs PowerShell 7 and the WinGet PowerShell module on the host (tray: Set up package search).</summary>
        public const string WingetUnavailable = "wingetUnavailable";

        /// <summary>403: the request came over the tailnet from a different node than the one this device is paired from.</summary>
        public const string TailscaleNodeMismatch = "tailscaleNodeMismatch";

        /// <summary>403: this device is tied to a Tailscale node, and the host's Tailscale client cannot say which node sent the request.</summary>
        public const string TailscaleNodeUnverified = "tailscaleNodeUnverified";

        /// <summary>409: none of the adapters in a wake relay request is on a network this host is on.</summary>
        public const string WakeTargetNotOnLan = "wakeTargetNotOnLan";

        /// <summary>409: the recovery key typed back, or entered for an existing target, does not match.</summary>
        public const string BadRecoveryKey = "badRecoveryKey";

        /// <summary>409: the recovery key offer expired or was never made; start the target's setup again.</summary>
        public const string RecoverySetupExpired = "recoverySetupExpired";

        /// <summary>409: the target's recovery key was already confirmed.</summary>
        public const string RecoveryKeyConfirmed = "recoveryKeyConfirmed";

        /// <summary>409: the storage holds no HyperHarbor backups.</summary>
        public const string NoBackupsFound = "noBackupsFound";

        /// <summary>404: no backup target has this id.</summary>
        public const string BackupTargetNotFound = "backupTargetNotFound";

        /// <summary>409: a backup, restore, or disk replacement was refused or failed; the detail says why.</summary>
        public const string BackupFailed = "backupFailed";

        /// <summary>409: a transfer block does not start where the file's verified part ends; expectedOffset says where.</summary>
        public const string TransferOffset = "transferOffset";

        /// <summary>409: the same VM is already being moved to this host.</summary>
        public const string TransferInProgress = "transferInProgress";

        /// <summary>409: a transfer was refused or failed; the detail says why.</summary>
        public const string TransferFailed = "transferFailed";
    }

    /// <summary>The Authorization scheme of the transferTicket security scheme: "HyperHarbor-Transfer &lt;ticket&gt;".</summary>
    public const string TransferTicketScheme = "HyperHarbor-Transfer";
}
