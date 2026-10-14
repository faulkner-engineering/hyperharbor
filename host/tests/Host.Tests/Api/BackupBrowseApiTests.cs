using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HyperHarbor.Host.Tests.Api;

public sealed class BackupBrowseApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _volume;
    private readonly TestHost _host;
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public BackupBrowseApiTests()
    {
        _volume = Directory.CreateDirectory(Path.Combine(_folder, "volume")).FullName;
        File.WriteAllText(Path.Combine(_volume, "hello.txt"), "hello from the backup");
        Directory.CreateDirectory(Path.Combine(_volume, "Docs"));
        var restorer = new FakeRestorer { Manifest = Manifest() };
        _host = new TestHost(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IBackupRepositories>(new FakeRepositories(_folder)));
            services.Replace(ServiceDescriptor.Singleton<ISnapshotRestorer>(restorer));
            services.Replace(ServiceDescriptor.Singleton<IVhdMounter>(new FolderMounter(_volume)));
        });
        _host.HyperVHost.Defaults = new HyperVDefaults(Path.Combine(_folder, "vms"), Path.Combine(_folder, "vhd"));
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
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
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static BackupManifest Manifest() => new()
    {
        HyperHarborVersion = "1.0.0",
        CreatedAt = new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero),
        Vm = new ManifestVm(Guid.NewGuid(), "Dev", 2, null),
        Compute = new ManifestCompute(1, 1024, null, false, false, false),
        Firmware = new ManifestFirmware(true, null),
        Tpm = false,
        Network = [],
        Disks = [new ManifestDisk("disk-0.vhdx", "scsi", 0, 0, 3)],
        Gpu = null,
        Performance = new ManifestPerformance(false, null, null),
        Consistency = new ManifestConsistency(ConsistencyMode.Off, []),
        Guest = new ManifestGuest("Windows", false),
    };

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

    private async Task<JsonObject> OpenReadyAsync()
    {
        var opened = await SendElevated(HttpMethod.Post, "/api/v1/backup/browse", new { targetId = "t1", snapshotId = "snap-1" });
        Assert.Equal(HttpStatusCode.Accepted, opened.StatusCode);
        var session = (await opened.Content.ReadFromJsonAsync<JsonObject>())!;
        for (var i = 0; i < 300; i++)
        {
            var current = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/backup/browse/{session["id"]}"))!;
            if ((string)current["state"]! != "preparing")
            {
                return current;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException();
    }

    [Fact]
    public async Task OpenBrowse_NeedsElevation()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/backup/browse", new { targetId = "t1", snapshotId = "snap-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OpenBrowse_PreparesTheSession_ThenFoldersAndFilesCanBeRead()
    {
        var session = await OpenReadyAsync();
        var id = (string)session["id"]!;
        Assert.Equal("ready", (string)session["state"]!);
        Assert.Equal("Dev", (string)session["vmName"]!);
        var drive = (string)session["volumes"]![0]!["drive"]!;

        var entries = await SendElevated(HttpMethod.Post, $"/api/v1/backup/browse/{id}/entries", new { volume = drive });
        var listing = (await entries.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Equal(["Docs", "hello.txt"], listing.Select(e => (string)e!["name"]!));
        Assert.True((bool)listing[0]!["isDirectory"]!);

        var file = await SendElevated(HttpMethod.Post, $"/api/v1/backup/browse/{id}/file", new { volume = drive, path = "hello.txt" });
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("hello from the backup", await file.Content.ReadAsStringAsync());
        Assert.Equal("hello.txt", file.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task APathOutsideTheVolume_Returns409()
    {
        var session = await OpenReadyAsync();
        var id = (string)session["id"]!;
        var drive = (string)session["volumes"]![0]!["drive"]!;

        var response = await SendElevated(HttpMethod.Post, $"/api/v1/backup/browse/{id}/file", new { volume = drive, path = @"..\..\secret.txt" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ASecondSession_Returns409_AndCloseAllowsANewOne()
    {
        var session = await OpenReadyAsync();
        var second = await SendElevated(HttpMethod.Post, "/api/v1/backup/browse", new { targetId = "t1", snapshotId = "snap-2" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var closed = await _client.DeleteAsync($"/api/v1/backup/browse/{session["id"]}");
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/backup/browse/{session["id"]}")).StatusCode);
        Assert.Equal("ready", (string)(await OpenReadyAsync())["state"]!);
    }

    [Fact]
    public async Task AnUnknownTarget_Returns404_AndAnUnknownSession_Returns404WithDetail()
    {
        var unknownTarget = await SendElevated(HttpMethod.Post, "/api/v1/backup/browse", new { targetId = "nope", snapshotId = "x" });
        var unknownSession = await _client.GetAsync("/api/v1/backup/browse/none");

        Assert.Equal(HttpStatusCode.NotFound, unknownTarget.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownSession.StatusCode);
        Assert.Contains("not open", await unknownSession.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListingWithoutElevation_Returns403()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/backup/browse/anything/entries", new { volume = "E:" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class FolderMounter(string volume) : IVhdMounter
    {
        public Task<IReadOnlyList<BrowseVolume>> MountAsync(string imagePath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrowseVolume>>([new BrowseVolume(volume, "Windows", 1000)]);

        public Task DismountAsync(string imagePath, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
