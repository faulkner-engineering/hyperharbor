using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Wake;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Api;

public sealed class WakeApiTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly HttpClient _client;

    public WakeApiTests()
    {
        using var certificate = TestHost.CreateClientCertificate();
        _host.Pair(certificate, "Living Room Laptop");
        _client = _host.CreateClient(certificate);
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Theory]
    [InlineData("GET", "/api/v1/wake/info")]
    [InlineData("GET", "/api/v1/wake/readiness")]
    [InlineData("POST", "/api/v1/wake/readiness/fix")]
    [InlineData("POST", "/api/v1/wake/test")]
    public async Task WakeEndpoints_RequirePairing(string method, string path)
    {
        using var anonymous = _host.CreateClient();

        var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Info_ReturnsWiredAdapters()
    {
        var info = await _client.GetFromJsonAsync<JsonObject>("/api/v1/wake/info");

        var adapter = Assert.Single(info!["adapters"]!.AsArray())!;
        Assert.Equal("90-2E-16-66-C5-AE", (string?)adapter["macAddress"]);
        Assert.Equal("192.168.1.255", (string?)adapter["broadcastAddress"]);
    }

    [Fact]
    public async Task Readiness_ReportsChecksInContractShape()
    {
        _host.Wake.Environment = WakeScenarios.WifiLaptop();

        var readiness = await _client.GetFromJsonAsync<JsonObject>("/api/v1/wake/readiness");

        Assert.False((bool?)readiness!["ready"]);
        var magicPacket = readiness["checks"]!.AsArray().Single(check => (string?)check!["id"] == "nicWakeOnMagicPacket")!;
        Assert.Equal("fail", (string?)magicPacket["status"]);
        Assert.True((bool?)magicPacket["autoFixAvailable"]);
    }

    [Fact]
    public async Task Fix_WhenNotElevated_AsksTrayAndReturns202()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/wake/readiness/fix", new { checkIds = new[] { "nicWakeOnMagicPacket", "nicAllowWake" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/api/v1/wake/readiness", response.Headers.Location?.OriginalString);
        var request = Assert.Single(_host.Tray.WakeFixRequests);
        Assert.Equal("Living Room Laptop", request.RequestedBy);
        Assert.Equal(["nicWakeOnMagicPacket", "nicAllowWake"], request.Fixes.Select(fix => fix.CheckId));
    }

    [Fact]
    public async Task Fix_WithoutTray_Returns503()
    {
        _host.Tray.CanDisplayPin = false;

        var response = await _client.PostAsJsonAsync("/api/v1/wake/readiness/fix", new { checkIds = new[] { "nicAllowWake" } });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("wiredAdapter")]
    [InlineData("madeUp")]
    public async Task Fix_UnfixableOrUnknownCheck_Returns400(string checkId)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/wake/readiness/fix", new { checkIds = new[] { checkId } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Tray.WakeFixRequests);
    }

    [Fact]
    public async Task Test_SchedulesSleep_AndRejectsSecondRequest()
    {
        var first = await _client.PostAsJsonAsync("/api/v1/wake/test", new { delaySeconds = 30 });
        var second = await _client.PostAsJsonAsync("/api/v1/wake/test", new { delaySeconds = 30 });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonObject>();
        var sleepAt = DateTimeOffset.Parse((string)body!["sleepAt"]!);
        Assert.InRange(sleepAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(0, _host.Sleep.SleepCount);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public async Task Test_DelayOutOfRange_Returns400(int delaySeconds)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/wake/test", new { delaySeconds });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed class WakeTestSchedulerTests
{
    [Fact]
    public void SleepsWhenDelayElapses_ThenAllowsAnotherTest()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sleep = new FakeSleepController();
        using var scheduler = new WakeTestScheduler(sleep, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<WakeTestScheduler>.Instance);

        scheduler.Schedule(TimeSpan.FromSeconds(15), "Laptop");
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(0, sleep.SleepCount);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, sleep.SleepCount);

        scheduler.Schedule(TimeSpan.FromSeconds(15), "Laptop");
    }
}
