using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Host.Tests.Updates;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Api;

public sealed class UpdateApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private readonly FakeReleaseServer _server = new();
    private TestHost? _host;
    private HttpClient? _client;

    public void Dispose()
    {
        _client?.Dispose();
        _host?.Dispose();
    }

    [Fact]
    public async Task APortableHost_ReportsUpdatesAsUnsupported()
    {
        var client = Start(installed: false);

        var status = await client.GetFromJsonAsync<JsonObject>("/api/v1/host/update");
        var check = await client.PostAsync("/api/v1/host/update/check", null);
        var settings = await SendElevatedAsync(HttpMethod.Put, "/api/v1/host/update/settings", new { channel = "stable", mode = "notify", maintenanceTime = (string?)null });

        Assert.False((bool)status!["supported"]!);
        Assert.Equal(["beta", "stable"], status["channels"]!.AsArray().Select(channel => (string)channel!));
        Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.UpdatesUnsupported, await CodeAsync(check));
        Assert.Equal(HttpStatusCode.Conflict, settings.StatusCode);
    }

    [Fact]
    public async Task TheInstalledHost_ReportsItsStatus_AndStartsACheck()
    {
        var client = Start(installed: true);

        var before = await client.GetFromJsonAsync<JsonObject>("/api/v1/host/update");
        var check = await client.PostAsync("/api/v1/host/update/check", null);

        Assert.True((bool)before!["supported"]!);
        Assert.Equal("auto", (string?)before["mode"]);
        Assert.Equal("idle", (string?)before["activity"]);
        Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
        Assert.Equal("/api/v1/host/update", check.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Settings_NeedElevation_AndAreSavedForTheHost()
    {
        var client = Start(installed: true);

        var unelevated = await client.PutAsJsonAsync("/api/v1/host/update/settings", new { channel = "beta", mode = "notify", maintenanceTime = "02:30" });
        var saved = await SendElevatedAsync(HttpMethod.Put, "/api/v1/host/update/settings", new { channel = "beta", mode = "notify", maintenanceTime = "02:30" });

        Assert.Equal(HttpStatusCode.Forbidden, unelevated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var status = await saved.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(("beta", "notify", "02:30"), ((string?)status!["channel"], (string?)status["mode"], (string?)status["maintenanceTime"]));
        var effective = _host!.Services.GetRequiredService<UpdateSettings>().Current();
        Assert.Equal(("beta", UpdateMode.Notify, "02:30"), (effective.Channel, effective.Mode, effective.MaintenanceTime));
    }

    [Theory]
    [InlineData("nightly", "03:00", "channel")]
    [InlineData("stable", "3am", "maintenanceTime")]
    public async Task InvalidSettings_AreRefusedWithTheField(string channel, string time, string field)
    {
        Start(installed: true);

        var response = await SendElevatedAsync(HttpMethod.Put, "/api/v1/host/update/settings", new { channel, mode = "auto", maintenanceTime = time });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray();
        Assert.Equal(field, (string?)errors.Single()!["field"]);
    }

    private HttpClient Start(bool installed)
    {
        _host = new TestHost(services =>
        {
            if (installed)
            {
                services.AddSingleton(provider =>
                {
                    var options = provider.GetRequiredService<UpdateOptions>();
                    var preparer = new UpdatePreparer(options, Releases.Downloader(_server, options), new UnsignedPackageVerifier(), new SelfTestGate(_host!.DataDirectory, new NoSelfTest(), TimeSpan.FromSeconds(5)), _host.DataDirectory);
                    return new UpdateCoordinator(
                        preparer,
                        new UpdateStateStore(_host.DataDirectory),
                        provider.GetRequiredService<HostActivity>(),
                        provider.GetRequiredService<UpdateSettings>().Current,
                        SemanticVersion.Parse("0.1.0"),
                        () => { },
                        TimeProvider.System,
                        NullLogger.Instance);
                });
            }
        });

        using var certificate = TestHost.CreateClientCertificate();
        _host.Pair(certificate, "Laptop");
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
        _client = _host.CreateClient(certificate);
        return _client;
    }

    private async Task<HttpResponseMessage> SendElevatedAsync(HttpMethod method, string path, object body)
    {
        var elevate = await _client!.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client!.SendAsync(request);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"];

    private sealed class NoSelfTest : ISelfTestProcess
    {
        public Task<(int ExitCode, string StandardError)?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<(int, string)?>((1, "not used"));
    }
}
