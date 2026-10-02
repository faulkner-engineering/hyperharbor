namespace HyperHarbor.Host.Service.Discovery;

/// <summary>
/// mDNS advertisement settings, bound from the "Discovery" configuration section.
/// </summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public bool Enabled { get; set; } = true;
}
