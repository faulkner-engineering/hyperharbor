using HyperHarbor.Host.Core.Discovery;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Host.Service.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Tests.Discovery;

public sealed class DiscoveryAdvertisementServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAdvertiser _advertiser = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAndStop_AdvertisesWithHostIdAndApiPort_ThenWithdraws()
    {
        var identity = new HostIdentityStore(_directory);
        var service = CreateService(identity, enabled: true);

        await service.StartAsync(CancellationToken.None);
        var ad = Assert.Single(_advertiser.Advertised);
        Assert.Equal(49000, ad.Port);
        Assert.Equal(identity.GetOrCreateHostId().ToString("D"), ad.Properties[ServiceAdvertisement.HostIdKey]);

        await service.StopAsync(CancellationToken.None);
        Assert.True(_advertiser.Withdrawn);
    }

    [Fact]
    public async Task Start_WhenDisabled_DoesNotAdvertise()
    {
        var service = CreateService(new HostIdentityStore(_directory), enabled: false);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(_advertiser.Advertised);
    }

    [Fact]
    public async Task Start_WhenAdvertiserFails_DoesNotThrow()
    {
        _advertiser.Fail = true;
        var service = CreateService(new HostIdentityStore(_directory), enabled: true);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    private DiscoveryAdvertisementService CreateService(HostIdentityStore identity, bool enabled) => new(
        _advertiser,
        identity,
        Options.Create(new ApiOptions { Port = 49000 }),
        Options.Create(new DiscoveryOptions { Enabled = enabled }),
        NullLogger<DiscoveryAdvertisementService>.Instance);

    private sealed class FakeAdvertiser : IServiceAdvertiser, IAsyncDisposable
    {
        public List<ServiceAdvertisement> Advertised { get; } = [];

        public bool Fail { get; set; }

        public bool Withdrawn { get; private set; }

        public Task<IAsyncDisposable> AdvertiseAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Registration failed.");
            }

            Advertised.Add(advertisement);
            return Task.FromResult<IAsyncDisposable>(this);
        }

        public ValueTask DisposeAsync()
        {
            Withdrawn = true;
            return ValueTask.CompletedTask;
        }
    }
}
