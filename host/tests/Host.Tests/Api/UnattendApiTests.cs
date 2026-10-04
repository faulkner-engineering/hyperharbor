using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Host.Tests.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class UnattendApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private const string SshKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDk4Zk2uK7xVQbV3mN6PZ8o2jQfV0uKZp1Gk3m8rT0aB user@laptop";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public UnattendApiTests()
    {
        TestIsos.Windows(Path.Combine(_host.IsoFolder, "win11.iso"), "Windows 11 Home", "Windows 11 Pro");
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    private static object LinuxProfile(string name = "Build Server") =>
        new { name, os = "linux", linux = new { sshAuthorizedKeys = new[] { SshKey }, packages = new[] { "git" }, installDesktop = false } };

    [Fact]
    public async Task List_ShowsTheBuiltInProfiles()
    {
        var profiles = (await _client.GetFromJsonAsync<JsonArray>("/api/v1/unattend-profiles"))!;

        Assert.Equal(["windows-workstation", "windows-burner", "ubuntu-dev-server"], profiles.Select(profile => (string?)profile!["id"]));
        Assert.All(profiles, profile => Assert.True((bool?)profile!["builtIn"]));
    }

    [Fact]
    public async Task Create_NeedsElevation()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/unattend-profiles", LinuxProfile());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(3, (await _client.GetFromJsonAsync<JsonArray>("/api/v1/unattend-profiles"))!.Count);
    }

    [Fact]
    public async Task CreateUpdateDelete_WithElevation_AuditsNameAndOsOnly()
    {
        var created = await SendElevated(HttpMethod.Post, "/api/v1/unattend-profiles", LinuxProfile());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profile = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = (string)profile["id"]!;
        Assert.Equal($"/api/v1/unattend-profiles/{id}", created.Headers.Location?.ToString());
        Assert.Equal(SshKey, (string?)profile["linux"]!["sshAuthorizedKeys"]![0]);

        var updated = await SendElevated(HttpMethod.Put, $"/api/v1/unattend-profiles/{id}", LinuxProfile("Renamed"));
        Assert.Equal("Renamed", (string?)(await updated.Content.ReadFromJsonAsync<JsonObject>())!["name"]);

        var deleted = await SendElevated(HttpMethod.Delete, $"/api/v1/unattend-profiles/{id}", null);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var entry = _host.AuditEntries().Last(item => (string?)item["action"] == "createUnattendProfile");
        Assert.Equal("name=Build Server, os=Linux", (string?)entry["detail"]);
        var audit = File.ReadAllText(Path.Combine(_host.DataDirectory, Core.Audit.FileAuditLog.FileName));
        Assert.DoesNotContain("AAAAC3NzaC1lZDI1NTE5", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltIns_CannotBeChanged()
    {
        var response = await SendElevated(HttpMethod.Put, "/api/v1/unattend-profiles/windows-burner", LinuxProfile());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(UnattendProfileStore.ProfileReadOnlyCode, (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
    }

    [Fact]
    public async Task InvalidProfile_Returns400WithFields()
    {
        var response = await SendElevated(HttpMethod.Post, "/api/v1/unattend-profiles",
            new { name = "Bad", os = "linux", adminAccountName = "Root", linux = new { sshAuthorizedKeys = new[] { "nope" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray().Select(issue => (string?)issue!["field"]).ToList();
        Assert.Contains("adminAccountName", fields);
        Assert.Contains("linux.sshAuthorizedKeys", fields);
    }

    [Fact]
    public async Task Inspection_ReportsTheImagesEditions()
    {
        var inspection = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/isos/win11.iso/inspection"))!;

        Assert.Equal("windows", (string?)inspection["os"]);
        Assert.Equal(["Windows 11 Home", "Windows 11 Pro"], inspection["editions"]!.AsArray().Select(edition => (string?)edition));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/isos/missing.iso/inspection")).StatusCode);
    }

    [Fact]
    public async Task Vms_ReportAnActiveInstall()
    {
        var vmId = Guid.NewGuid();
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(vmId, "Dev Box", VmState.Running));
        var now = DateTimeOffset.UtcNow;
        var installs = _host.Services.GetRequiredService<UnattendedInstallStore>();
        installs.Save(new UnattendedInstall(vmId, Guid.NewGuid(), "windows-workstation", InstallOs.Windows, false, "seed.iso",
            UnattendedInstallState.WaitingForGuest, "Waiting for Windows", now, now));

        var vm = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{vmId}"))!;
        Assert.Equal("waitingForGuest", (string?)vm["installState"]);

        installs.Save(installs.Find(vmId)! with { State = UnattendedInstallState.Ready });
        vm = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{vmId}"))!;
        Assert.Null(vm["installState"]);
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
}
