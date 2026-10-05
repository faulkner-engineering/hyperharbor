using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class UpdateGateApiTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly HttpClient _client;

    public UpdateGateApiTests()
    {
        using var certificate = TestHost.CreateClientCertificate();
        _host.Pair(certificate, "Laptop");
        _client = _host.CreateClient(certificate);
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task WhileAnUpdateInstalls_ChangesAreRefused_AndReadsStillWork()
    {
        Assert.True(_host.Services.GetRequiredService<HostActivity>().TryClose());

        var change = await _client.PostAsJsonAsync("/api/v1/wake/test", new { delaySeconds = 30 });
        var read = await _client.GetAsync("/api/v1/vms");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, change.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.Updating, (string?)(await change.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
        Assert.Equal(TimeSpan.FromSeconds(120), change.Headers.RetryAfter?.Delta);
        Assert.Equal(0, _host.Sleep.SleepCount);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task AChangeInProgress_KeepsTheGateFromClosing()
    {
        var activity = _host.Services.GetRequiredService<HostActivity>();

        var response = await _client.PostAsJsonAsync("/api/v1/wake/test", new { delaySeconds = 30 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(activity.InProgress);
        Assert.True(activity.TryClose());
    }
}
