using System.ComponentModel.DataAnnotations;
using System.Net;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// API listener settings, bound from the "Api" configuration section.
/// </summary>
public sealed class ApiOptions : IValidatableObject
{
    public const string SectionName = "Api";

    /// <summary>
    /// TCP port for the HTTPS API.
    /// </summary>
    [Range(1024, 65535)]
    public int Port { get; set; } = 48443;

    /// <summary>
    /// IP address to listen on. Empty (the default) listens on all interfaces. Tests set a loopback
    /// address so they do not open the API to the network.
    /// </summary>
    public string? ListenAddress { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(ListenAddress) && !IPAddress.TryParse(ListenAddress, out _))
        {
            yield return new ValidationResult($"{ListenAddress} is not an IP address.", [nameof(ListenAddress)]);
        }
    }
}
