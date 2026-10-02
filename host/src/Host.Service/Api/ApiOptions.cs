using System.ComponentModel.DataAnnotations;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// API listener settings, bound from the "Api" configuration section.
/// </summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// TCP port for the HTTPS API, bound on all interfaces.
    /// </summary>
    [Range(1024, 65535)]
    public int Port { get; set; } = 48443;
}
