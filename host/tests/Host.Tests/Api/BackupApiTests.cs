using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class BackupApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private const string SnapshotJson =
        """{"id":"k1","source":{"host":"PC","userName":"SYSTEM","path":"C:\\x"},"startTime":"2026-10-12T02:00:00Z","endTime":"2026-10-12T02:01:00Z","stats":{"totalSize":10}}""";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly FakeKopiaRunner _kopia = new();
    private readonly TestHost _host;
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public BackupApiTests()
    {
        _host = new TestHost(services => services.AddSingleton(new KopiaCli(_kopia)));
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Off));
        _kopia.Respond = invocation => invocation.Arguments.Contains("list")
            ? new KopiaResult(0, "[" + SnapshotJson.Replace("C:\\\\x", $@"D:\\vms\\.hh-backup\\{VmId:N}").Replace("\"id\":\"k1\"", "\"id\":\"k1\",\"description\":\"hyperharbor vm=" + VmId + " name=Dev Box run=aa\"") + "]", string.Empty)
            : invocation.Arguments.Contains("create") && invocation.Arguments.Contains("snapshot")
                ? new KopiaResult(0, SnapshotJson, string.Empty)
                : new KopiaResult(0, string.Empty, string.Empty);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private object LocalTarget(string name = "Local") => new { name, kind = "local", local = new { path = _folder }, schedule = new { time = "03:30" } };

    private async Task<JsonObject> CreateTargetAsync(string name = "Local")
    {
        var response = await SendElevated(HttpMethod.Post, "/api/v1/backup/targets", LocalTarget(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private async Task<JsonObject> ConfirmedTargetAsync()
    {
        var target = await CreateTargetAsync();
        var offer = (await (await SendElevated(HttpMethod.Post, $"/api/v1/backup/targets/{target["id"]}/recovery-key", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        var key = (string)offer["recoveryKey"]!;
        var typed = string.Join('-', key.Split('-')[^4..]);
        var confirmed = await SendElevated(HttpMethod.Post, "/api/v1/backup/recovery-key/confirm", new { setupId = offer["setupId"], typedBack = typed });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        return (await confirmed.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task CreateTarget_NeedsElevation()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/backup/targets", LocalTarget());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>("/api/v1/backup/targets"))!);
    }

    [Fact]
    public async Task CreateTarget_StartsWithAPendingKey_AndTheListShowsIt()
    {
        var created = await CreateTargetAsync();

        Assert.Equal("pendingKey", (string?)created["keyState"]);
        Assert.Equal("03:30", (string?)created["schedule"]!["time"]);
        Assert.Equal(7, (int?)created["retention"]!["daily"]);
        var list = (await _client.GetFromJsonAsync<JsonArray>("/api/v1/backup/targets"))!;
        Assert.Equal((string?)created["id"], (string?)list.Single()!["id"]);
    }

    [Fact]
    public async Task CreateTarget_WithABadTimeOrNoKeys_Returns400()
    {
        var badTime = await SendElevated(HttpMethod.Post, "/api/v1/backup/targets", new { name = "x", kind = "local", local = new { path = _folder }, schedule = new { time = "25:00" } });
        var noKeys = await SendElevated(HttpMethod.Post, "/api/v1/backup/targets", new { name = "x", kind = "s3", s3 = new { endpoint = "s3.example.com", bucket = "b" } });

        Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noKeys.StatusCode);
    }

    [Fact]
    public async Task RemoteKeys_AreNeverReturnedOrAudited()
    {
        var response = await SendElevated(HttpMethod.Post, "/api/v1/backup/targets", new
        {
            name = "Cloud",
            kind = "s3",
            s3 = new { endpoint = "s3.example.com", bucket = "b" },
            credentials = new { s3AccessKey = "AKIA-LEAK-CANARY", s3SecretKey = "secret-LEAK-CANARY" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var list = await _client.GetStringAsync("/api/v1/backup/targets");
        var audit = string.Join('\n', _host.AuditEntries().Select(entry => entry.ToJsonString()));
        Assert.DoesNotContain("LEAK-CANARY", body + list + audit, StringComparison.Ordinal);
        _host.Logs.AssertNoneContain("LEAK-CANARY");
    }

    [Fact]
    public async Task RecoveryKey_IsShownOnce_AndConfirmedByTypingTheEndBack()
    {
        var target = await CreateTargetAsync();
        var path = $"/api/v1/backup/targets/{target["id"]}/recovery-key";
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync(path, null)).StatusCode);
        var offer = (await (await SendElevated(HttpMethod.Post, path, null)).Content.ReadFromJsonAsync<JsonObject>())!;

        var wrong = await SendElevated(HttpMethod.Post, "/api/v1/backup/recovery-key/confirm", new { setupId = offer["setupId"], typedBack = "AAAA-AAAA-AAAA-AAAA" });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.BadRecoveryKey, (string?)(await wrong.Content.ReadFromJsonAsync<JsonObject>())!["code"]);

        var confirmed = await ConfirmedFromOffer(offer);
        Assert.Equal("confirmed", (string?)confirmed["keyState"]);

        var again = await SendElevated(HttpMethod.Post, path, null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.RecoveryKeyConfirmed, (string?)(await again.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
    }

    [Fact]
    public async Task ConfirmingAnUnknownSetup_ReportsItExpired()
    {
        var response = await SendElevated(HttpMethod.Post, "/api/v1/backup/recovery-key/confirm", new { setupId = "nope", typedBack = "AAAA-AAAA-AAAA-AAAA" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.RecoverySetupExpired, (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
    }

    [Fact]
    public async Task TestTarget_ChecksTheFolder_AndAnUnknownTargetIs404()
    {
        var target = await CreateTargetAsync();

        var result = (await (await _client.PostAsync($"/api/v1/backup/targets/{target["id"]}/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        var unknown = await _client.PostAsync("/api/v1/backup/targets/missing/test", null);

        Assert.NotNull(result["passed"]);
        Assert.NotEmpty(result["steps"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.BackupTargetNotFound, (string?)(await unknown.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
    }

    [Fact]
    public async Task UpdateAndDeleteTarget_NeedElevation_AndDeleteRemovesItsPolicies()
    {
        var target = await ConfirmedTargetAsync();
        var path = $"/api/v1/backup/targets/{target["id"]}";
        await _client.PutAsJsonAsync("/api/v1/backup/policies", new { vmId = VmId, targetId = target["id"] });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PatchAsJsonAsync(path, new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.DeleteAsync(path)).StatusCode);

        var renamed = (await (await SendElevated(HttpMethod.Patch, path, new { name = "Renamed", enabled = false })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("Renamed", (string?)renamed["name"]);
        Assert.False((bool?)renamed["enabled"]);

        Assert.Equal(HttpStatusCode.NoContent, (await SendElevated(HttpMethod.Delete, path, null)).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>("/api/v1/backup/targets"))!);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>("/api/v1/backup/policies"))!);
    }

    [Fact]
    public async Task Policies_AreSetListedAndRemoved_WithTheTargetsDefaults()
    {
        var target = await ConfirmedTargetAsync();

        var set = await _client.PutAsJsonAsync("/api/v1/backup/policies", new { vmId = VmId, targetId = target["id"], triggers = new { onShutdown = true, debounceMinutes = 15 } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var policy = (await set.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(7, (int?)policy["retention"]!["daily"]);
        Assert.True((bool?)policy["triggers"]!["onShutdown"]);
        Assert.Equal(15, (int?)policy["triggers"]!["debounceMinutes"]);
        Assert.Single((await _client.GetFromJsonAsync<JsonArray>("/api/v1/backup/policies"))!);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/v1/backup/policies/{target["id"]}/{VmId}")).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>("/api/v1/backup/policies"))!);
    }

    [Fact]
    public async Task SetPolicy_ForAnUnknownTarget_Returns404()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/backup/policies", new { vmId = VmId, targetId = "missing" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ManualHostStateRun_IsAJobThatFillsTheHistoryAndStatus()
    {
        var target = await ConfirmedTargetAsync();

        var started = await _client.PostAsJsonAsync("/api/v1/backup/runs", new { targetId = target["id"] });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var job = (await started.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("backupHostState", (string?)job["kind"]);
        await WaitUntil(async () => (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!["state"] != "running");
        var finished = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!;
        Assert.Equal("succeeded", (string?)finished["state"]);

        var history = (await _client.GetFromJsonAsync<JsonArray>("/api/v1/backup/history"))!;
        var run = history.Single()!;
        Assert.Equal("host", (string?)run["item"]);
        Assert.Equal("manual", (string?)run["trigger"]);
        Assert.Equal("succeeded", (string?)run["outcome"]);
        var status = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/backup/status"))!;
        Assert.Equal("host", (string?)status["items"]![0]!["item"]);
        Assert.False((bool?)status["items"]![0]!["overdue"]);
        Assert.Equal("succeeded", (string?)status["items"]![0]!["lastRun"]!["outcome"]);
    }

    [Fact]
    public async Task ManualRun_RefusesAnUnknownVmAndAnUnreadyTarget()
    {
        var pending = await CreateTargetAsync();
        var confirmed = await ConfirmedTargetAsync2();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/v1/backup/runs", new { targetId = confirmed["id"], vmId = Guid.NewGuid() })).StatusCode);
        var unready = await _client.PostAsJsonAsync("/api/v1/backup/runs", new { targetId = pending["id"] });
        Assert.Equal(HttpStatusCode.Conflict, unready.StatusCode);
    }

    [Fact]
    public async Task Scan_NeedsElevation_AndGroupsTheSnapshotsByVm()
    {
        var target = await ConfirmedTargetAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/backup/scan", new { targetId = target["id"] })).StatusCode);

        var scan = await SendElevated(HttpMethod.Post, "/api/v1/backup/scan", new { targetId = target["id"] });

        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var catalog = (await scan.Content.ReadFromJsonAsync<JsonObject>())!;
        var vm = catalog["vms"]!.AsArray().Single()!;
        Assert.Equal(VmId.ToString(), (string?)vm["vmId"]);
        Assert.Equal("Dev Box", (string?)vm["name"]);
        Assert.Equal("k1", (string?)vm["snapshots"]![0]!["id"]);
    }

    [Fact]
    public async Task Restore_NeedsElevation_AndAnUnknownTargetIs404()
    {
        var body = new { targetId = "missing", snapshotId = "k1" };

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/backup/restore", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendElevated(HttpMethod.Post, "/api/v1/backup/restore", body)).StatusCode);
    }

    [Fact]
    public async Task Restore_OfASnapshotWithoutAManifest_FailsTheJobWithAReadableReason()
    {
        var target = await ConfirmedTargetAsync();

        var started = await SendElevated(HttpMethod.Post, "/api/v1/backup/restore", new { targetId = target["id"], snapshotId = "k1", destination = _folder });

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var job = (await started.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("restoreVm", (string?)job["kind"]);
        await WaitUntil(async () => (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!["state"] != "running");
        var finished = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!;
        Assert.Equal("failed", (string?)finished["state"]);
        Assert.Contains("manifest", (string?)finished["error"]!["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_WithARelativeDestination_Returns400()
    {
        var target = await ConfirmedTargetAsync();

        var response = await SendElevated(HttpMethod.Post, "/api/v1/backup/restore", new { targetId = target["id"], snapshotId = "k1", destination = "vms" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceDisk_NeedsElevation_AndRequiresTheTypedName()
    {
        var target = await ConfirmedTargetAsync();
        var path = $"/api/v1/vms/{VmId}/replace-disk";
        var body = new { targetId = target["id"], snapshotId = "k1", typedName = "dev box" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync(path, body)).StatusCode);

        var started = await SendElevated(HttpMethod.Post, path, body);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var job = (await started.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("replaceDisk", (string?)job["kind"]);
        await WaitUntil(async () => (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!["state"] != "running");
        var finished = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!;
        Assert.Equal("failed", (string?)finished["state"]);
        Assert.Contains("typed name", (string?)finished["error"]!["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HostStateRestore_NeedsElevation()
    {
        var target = await ConfirmedTargetAsync();
        var body = new { targetId = target["id"], snapshotId = "k1" };

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/backup/restore/host-state", body)).StatusCode);
        var started = await SendElevated(HttpMethod.Post, "/api/v1/backup/restore/host-state", body);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal("restoreHostState", (string?)(await started.Content.ReadFromJsonAsync<JsonObject>())!["kind"]);
    }

    private Task<JsonObject> ConfirmedTargetAsync2() => ConfirmedTargetNamedAsync("Second");

    private async Task<JsonObject> ConfirmedTargetNamedAsync(string name)
    {
        var target = await CreateTargetAsync(name);
        var offer = (await (await SendElevated(HttpMethod.Post, $"/api/v1/backup/targets/{target["id"]}/recovery-key", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        return await ConfirmedFromOffer(offer);
    }

    private async Task<JsonObject> ConfirmedFromOffer(JsonObject offer)
    {
        var typed = string.Join('-', ((string)offer["recoveryKey"]!).Split('-')[^4..]);
        var response = await SendElevated(HttpMethod.Post, "/api/v1/backup/recovery-key/confirm", new { setupId = offer["setupId"], typedBack = typed });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private async Task<HttpResponseMessage> SendElevated(HttpMethod method, string path, object? body)
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(20);
        }
    }
}
