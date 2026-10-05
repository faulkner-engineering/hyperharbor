using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.RemoteDesktop;

/// <summary>The host's own Remote Desktop settings as read from Windows.</summary>
/// <param name="EditionId">EditionID from the registry, for example "Professional" or "Core".</param>
public sealed record RemoteDesktopState(string EditionId, string ProductName, bool Enabled, int Port, bool FirewallOpen);

/// <summary>Reads and changes the host's Remote Desktop settings.</summary>
public interface IRemoteDesktopSettings
{
    RemoteDesktopState Read();

    /// <summary>Allows Remote Desktop connections and enables the firewall exception. Needs administrator rights.</summary>
    void Allow();
}

/// <summary>A request about the host's Remote Desktop that cannot be done (409 with a problem code).</summary>
public sealed class RemoteDesktopConflictException : Exception
{
    public RemoteDesktopConflictException(string message, string code)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Reports the host's Remote Desktop state and turns it on when asked.</summary>
public sealed class HostRemoteDesktopService
{
    private readonly IRemoteDesktopSettings _settings;
    private readonly ILogger<HostRemoteDesktopService> _logger;
    private readonly bool _isElevated;

    public HostRemoteDesktopService(IRemoteDesktopSettings settings, ILogger<HostRemoteDesktopService> logger)
        : this(settings, logger, Environment.IsPrivilegedProcess)
    {
    }

    internal HostRemoteDesktopService(IRemoteDesktopSettings settings, ILogger<HostRemoteDesktopService> logger, bool isElevated)
    {
        _settings = settings;
        _logger = logger;
        _isElevated = isElevated;
    }

    /// <summary>Home editions have no Remote Desktop server; their EditionID starts with "Core".</summary>
    public static bool IsSupportedEdition(string editionId) =>
        !editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    public HostRemoteDesktop Get() => Map(_settings.Read());

    /// <exception cref="RemoteDesktopConflictException">The edition has no Remote Desktop server, or the host process lacks administrator rights.</exception>
    public HostRemoteDesktop Enable()
    {
        var state = _settings.Read();
        if (!IsSupportedEdition(state.EditionId))
        {
            throw new RemoteDesktopConflictException(
                $"{state.ProductName} cannot accept Remote Desktop connections. Remote Desktop needs Windows Pro, Enterprise, or Education.",
                ContractInfo.ProblemCodes.RemoteDesktopUnsupported);
        }

        if (state.Enabled && state.FirewallOpen)
        {
            return Map(state);
        }

        if (!_isElevated)
        {
            throw new RemoteDesktopConflictException(
                "This host runs without administrator rights, so it cannot turn on Remote Desktop. Turn it on at the host in Settings > System > Remote Desktop, or install the host service.",
                ContractInfo.ProblemCodes.RequiresInstalledService);
        }

        _settings.Allow();
        var after = _settings.Read();
        _logger.LogInformation("Remote Desktop to the host turned on (enabled {Enabled}, firewall open {FirewallOpen}, port {Port}).", after.Enabled, after.FirewallOpen, after.Port);
        return Map(after);
    }

    private static HostRemoteDesktop Map(RemoteDesktopState state) =>
        new(IsSupportedEdition(state.EditionId), state.Enabled, state.Port, state.FirewallOpen, state.ProductName);
}
