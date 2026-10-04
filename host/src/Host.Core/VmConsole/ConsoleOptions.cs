using System.ComponentModel.DataAnnotations;
using System.Net;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>The "Console" configuration section.</summary>
public sealed class ConsoleOptions
{
    public const string SectionName = "Console";

    /// <summary>
    /// Where tunnels connect: the host's Virtual Machine Connection service. Tests point it elsewhere.
    /// It stays on loopback, so port 2179 is never exposed beyond what Hyper-V itself configures.
    /// </summary>
    [Required]
    public string Endpoint { get; set; } = "127.0.0.1:2179";

    [Range(10, 600)]
    public int ReuseWindowSeconds { get; set; } = (int)ConsolePasswordRotator.DefaultReuseWindow.TotalSeconds;

    [Range(10, 600)]
    public int TicketLifetimeSeconds { get; set; } = (int)ConsoleTicketStore.DefaultLifetime.TotalSeconds;

    public IPEndPoint EndpointAddress => IPEndPoint.Parse(Endpoint);
}
