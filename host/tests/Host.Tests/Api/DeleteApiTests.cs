using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class DeleteApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private const string Disk = @"C:\VMs\Dev\Dev.vhdx";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public DeleteApiTests()
    {
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _host.Storage.Attach(VmId, "Dev", Disk);
        _host.DiskFiles.Files.Add(Disk);
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

    [Fact]
    public async Task Preview_ListsTheDisk()
    {
        var preview = await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/delete-preview");

        Assert.Equal("Dev", (string?)preview!["vmName"]);
        Assert.Equal("off", (string?)preview["state"]);
        Assert.Equal(Disk, (string?)preview["disks"]![0]);
        Assert.Empty(preview["blockers"]!.AsArray());
    }

    [Fact]
    public async Task Delete_WithoutElevation_Returns403_AndDeletesNothing()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/delete", Request(deleteDisks: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Storage.DeletedVms);
    }

    [Fact]
    public async Task Delete_StartsAJob_ThatDeletesTheVmAndDisk()
    {
        var response = await SendElevated(Request(deleteDisks: true));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("deleteVm", (string?)job["kind"]);
        Assert.Equal($"/api/v1/jobs/{(string?)job["id"]}", response.Headers.Location?.OriginalString);

        var finished = await WaitForJobAsync((string)job["id"]!);
        Assert.Equal("succeeded", (string?)finished["state"]);
        Assert.Equal(100, (int?)finished["percentComplete"]);
        Assert.Null(finished["error"]);
        Assert.Equal([VmId], _host.Storage.DeletedVms);
        Assert.Equal([Disk], _host.DiskFiles.Deleted);
    }

    [Fact]
    public async Task Delete_RunningVm_Returns409()
    {
        _host.Inventory.SetState(VmId, VmState.Running);

        var response = await SendElevated(Request(deleteDisks: false));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_host.Storage.DeletedVms);
    }

    [Fact]
    public async Task Delete_SharedParentDisk_Returns409()
    {
        const string child = @"C:\VMs\Web\Web.vhdx";
        _host.Storage.Attach(Guid.NewGuid(), "Web", child);
        _host.Storage.Parents[child] = Disk;

        var response = await SendElevated(Request(deleteDisks: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("Web", (string?)problem!["detail"], StringComparison.Ordinal);
        Assert.Empty(_host.DiskFiles.Deleted);
    }

    [Fact]
    public async Task Delete_WithTheWrongName_Returns400WithTheField()
    {
        var response = await SendElevated(new { deleteDisks = false, deleteCheckpoints = false, confirmName = "dev" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("confirmName", (string?)problem!["errors"]![0]!["field"]);
    }

    [Fact]
    public async Task Delete_WithoutTheFlags_Returns400()
    {
        var response = await SendElevated(new { confirmName = "Dev" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Storage.DeletedVms);
    }

    [Fact]
    public async Task Delete_IsAudited_IncludingTheJobOutcome()
    {
        var response = await SendElevated(Request(deleteDisks: true));
        var jobId = (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        await WaitForJobAsync(jobId);

        // The job's own entry is written just after its state changes, so wait for it.
        await WaitUntil(() => _host.AuditEntries().Count(entry => (string?)entry["action"] == "deleteVm") == 3);
        var entries = _host.AuditEntries().Where(entry => (string?)entry["action"] == "deleteVm").ToList();
        Assert.Equal(["requested", "succeeded", "succeeded"], entries.Select(entry => (string?)entry["outcome"]));
        Assert.All(entries, entry =>
        {
            Assert.Equal("Dev", (string?)entry["vmName"]);
            Assert.Equal("Laptop", (string?)entry["deviceName"]);
        });

        // The requested entry is written before the elevation check runs.
        Assert.False((bool?)entries[0]["elevated"]);
        Assert.All(entries.Skip(1), entry => Assert.True((bool?)entry["elevated"]));
        Assert.Equal(jobId, (string?)entries[2]["jobId"]);
        Assert.StartsWith("Job completed", (string?)entries[2]["detail"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownJob_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object Request(bool deleteDisks) => new { deleteDisks, deleteCheckpoints = false, confirmName = "Dev" };

    private async Task<HttpResponseMessage> SendElevated(object body)
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/vms/{VmId}/delete") { Content = JsonContent.Create(body) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }

    private async Task<JsonObject> WaitForJobAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var job = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{jobId}"))!;
            if ((string?)job["state"] != "running")
            {
                return job;
            }

            Assert.True(DateTime.UtcNow < deadline, "The job did not finish.");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20);
        }
    }
}
