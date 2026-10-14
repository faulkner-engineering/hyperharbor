using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HyperHarbor.Host.Tests.Api;

public sealed class RestoreTestApiTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private readonly TestHost _host;
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;
    private readonly CountingRestorer _restorer = new();

    public RestoreTestApiTests()
    {
        _host = new TestHost(services => services.Replace(ServiceDescriptor.Singleton<IVmRestorer>(_restorer)));
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    private void SeedBackup()
    {
        _host.Services.GetRequiredService<BackupConfigStore>().Update(_ => new BackupConfig
        {
            Targets =
            [
                new BackupTarget
                {
                    Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B"),
                    VmRepository = new RepositoryRef("r1", RepositoryKeyState.Confirmed), HostRepository = new RepositoryRef("r2", RepositoryKeyState.Confirmed),
                },
            ],
        });
        var now = DateTimeOffset.UtcNow;
        _host.Services.GetRequiredService<BackupHistoryStore>().Add(new BackupRunRecord(
            Guid.NewGuid(), $"vm:{VmId:D}", VmId, "Dev Box", "t1", BackupTrigger.Schedule, now, now, BackupOutcome.Succeeded, null, "snap-1", 1024, []));
    }

    [Fact]
    public async Task Status_StartsWithTheDefaults_AndNothingToTest()
    {
        var status = (await (await _client.GetAsync("/api/v1/backup/restore-test")).Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.True((bool)status["settings"]!["enabled"]!);
        Assert.Equal(30, (int)status["settings"]!["intervalDays"]!);
        Assert.Equal("03:00", (string)status["settings"]!["window"]!["start"]!);
        Assert.False((bool)status["running"]!);
        Assert.Equal(0, (int)status["testable"]!);
        Assert.Empty(status["results"]!.AsArray());
    }

    [Fact]
    public async Task Settings_AreSaved_ValidatedAndShownBack()
    {
        var saved = await _client.PutAsJsonAsync("/api/v1/backup/restore-test/settings", new { enabled = false, intervalDays = 14, window = new { start = "01:00", end = "02:30" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var status = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/backup/restore-test"))!;
        Assert.False((bool)status["settings"]!["enabled"]!);
        Assert.Equal(14, (int)status["settings"]!["intervalDays"]!);
        Assert.Null(status["nextDue"]);

        var tooOften = await _client.PutAsJsonAsync("/api/v1/backup/restore-test/settings", new { enabled = true, intervalDays = 0, window = new { start = "01:00", end = "02:30" } });
        Assert.Equal(HttpStatusCode.BadRequest, tooOften.StatusCode);
        var badTime = await _client.PutAsJsonAsync("/api/v1/backup/restore-test/settings", new { enabled = true, intervalDays = 30, window = new { start = "1am", end = "02:30" } });
        Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);
    }

    [Fact]
    public async Task RunNow_WithoutABackup_Returns409()
    {
        var response = await _client.PostAsync("/api/v1/backup/restore-test/run", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, _restorer.Calls);
    }

    [Fact]
    public async Task RunNow_TestsTheBackup_AndTheResultAppearsInTheStatus()
    {
        SeedBackup();

        var started = await _client.PostAsync("/api/v1/backup/restore-test/run", null);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        await _host.Services.GetRequiredService<RestoreTestService>().WhenIdle();
        var status = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/backup/restore-test"))!;
        Assert.Equal(1, (int)status["testable"]!);
        var result = Assert.Single(status["results"]!.AsArray())!;
        Assert.Equal("Dev Box", (string)result["vmName"]!);
        Assert.Equal("failed", (string)result["outcome"]!);
        Assert.Equal("manual", (string)result["trigger"]!);
        Assert.Equal("The backup is missing the disk.", (string)result["reason"]!);
        Assert.Equal(1, _restorer.Calls);
    }

    [Fact]
    public async Task RunNow_IsAudited_WithoutDetails()
    {
        SeedBackup();

        await _client.PostAsync("/api/v1/backup/restore-test/run", null);
        await _host.Services.GetRequiredService<RestoreTestService>().WhenIdle();

        var audit = File.ReadAllText(Path.Combine(_host.DataDirectory, "audit.log"));
        Assert.Contains("startRestoreTest", audit);
    }

    private sealed class CountingRestorer : IVmRestorer
    {
        public int Calls { get; private set; }

        public Task<RestoreVmResult> RestoreAsync(RestoreVmRequest request, Action<string, int> progress, CancellationToken cancellationToken)
        {
            Calls++;
            throw new RestoreException("The backup is missing the disk.");
        }
    }
}
