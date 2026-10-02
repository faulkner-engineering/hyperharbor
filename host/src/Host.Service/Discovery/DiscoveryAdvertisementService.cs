using System.Reflection;
using HyperHarbor.Host.Core.Discovery;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Service.Discovery;

/// <summary>
/// Advertises this host as _hyperharbor._tcp for the lifetime of the service. A failed
/// registration is logged and does not stop the service; clients can still add the host manually.
/// </summary>
public sealed class DiscoveryAdvertisementService : IHostedService
{
    private readonly IServiceAdvertiser _advertiser;
    private readonly HostIdentityStore _identity;
    private readonly IOptions<ApiOptions> _apiOptions;
    private readonly IOptions<DiscoveryOptions> _discoveryOptions;
    private readonly ILogger<DiscoveryAdvertisementService> _logger;
    private IAsyncDisposable? _registration;

    public DiscoveryAdvertisementService(
        IServiceAdvertiser advertiser,
        HostIdentityStore identity,
        IOptions<ApiOptions> apiOptions,
        IOptions<DiscoveryOptions> discoveryOptions,
        ILogger<DiscoveryAdvertisementService> logger)
    {
        _advertiser = advertiser;
        _identity = identity;
        _apiOptions = apiOptions;
        _discoveryOptions = discoveryOptions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_discoveryOptions.Value.Enabled)
        {
            _logger.LogInformation("mDNS advertisement is disabled by configuration.");
            return;
        }

        var advertisement = ServiceAdvertisement.Create(
            Environment.MachineName,
            (ushort)_apiOptions.Value.Port,
            _identity.GetOrCreateHostId(),
            ContractInfo.ApiVersion,
            HostVersion);

        try
        {
            _registration = await _advertiser.AdvertiseAsync(advertisement, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "mDNS advertisement failed; clients must add this host manually.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_registration is not null)
        {
            await _registration.DisposeAsync();
            _registration = null;
        }
    }

    private static string HostVersion =>
        typeof(DiscoveryAdvertisementService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
