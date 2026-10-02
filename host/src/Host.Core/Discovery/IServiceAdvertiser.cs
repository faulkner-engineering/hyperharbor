namespace HyperHarbor.Host.Core.Discovery;

/// <summary>
/// Publishes a DNS-SD service instance on the local network.
/// </summary>
public interface IServiceAdvertiser
{
    /// <summary>Publishes the advertisement. Dispose the returned handle to withdraw it.</summary>
    Task<IAsyncDisposable> AdvertiseAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken);
}
