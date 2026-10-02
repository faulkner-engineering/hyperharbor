namespace HyperHarbor.Host.Core.Discovery;

/// <summary>
/// A DNS-SD service instance to publish over mDNS.
/// </summary>
/// <param name="InstanceName">Human-readable instance label, for example the computer name.</param>
/// <param name="HostName">Host name without the .local suffix.</param>
public sealed record ServiceAdvertisement(
    string InstanceName,
    string HostName,
    ushort Port,
    IReadOnlyDictionary<string, string> Properties)
{
    public const string ServiceType = "_hyperharbor._tcp";

    // TXT record keys. Keep these short; they are part of the discovery contract with the client.
    public const string HostIdKey = "id";
    public const string ApiVersionKey = "api";
    public const string HostVersionKey = "ver";

    /// <summary>Fully qualified instance name, for example GAMING-PC._hyperharbor._tcp.local.</summary>
    public string FullInstanceName => $"{InstanceName}.{ServiceType}.local";

    public string FullHostName => $"{HostName}.local";

    public static ServiceAdvertisement Create(string machineName, ushort port, Guid hostId, string apiVersion, string hostVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        // DNS labels are limited to 63 bytes; dots would split the instance label.
        var instance = machineName.Replace('.', '-');
        if (instance.Length > 63)
        {
            instance = instance[..63];
        }

        return new ServiceAdvertisement(
            instance,
            machineName,
            port,
            new Dictionary<string, string>
            {
                [HostIdKey] = hostId.ToString("D"),
                [ApiVersionKey] = apiVersion,
                [HostVersionKey] = hostVersion,
            });
    }
}
