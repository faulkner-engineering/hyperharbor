using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

/// <summary>Audit entries written around state-changing requests.</summary>
public sealed class AuditApiTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();

    public void Dispose() => _certificate.Dispose();

    [Fact]
    public async Task PowerAction_WritesRequestedAndSucceeded_WithUserDeviceAndVm()
    {
        using var host = new TestHost();
        host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Off));
        var device = host.Pair(_certificate, "Laptop");
        using var client = host.CreateClient(_certificate);

        var response = await client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "start" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var entries = host.AuditEntries();
        Assert.Equal(["requested", "succeeded"], entries.Select(entry => (string?)entry["outcome"]));
        var owner = host.Services.GetRequiredService<UserStore>().GetOrCreateDefault();
        Assert.All(entries, entry =>
        {
            Assert.Equal("performVmAction", (string?)entry["action"]);
            Assert.Equal(owner.UserId.ToString(), (string?)entry["userId"]);
            Assert.Equal(owner.Name, (string?)entry["userName"]);
            Assert.Equal(device.DeviceId.ToString(), (string?)entry["deviceId"]);
            Assert.Equal("Laptop", (string?)entry["deviceName"]);
            Assert.Equal(VmId.ToString(), (string?)entry["vmId"]);
            Assert.Equal("Dev Box", (string?)entry["vmName"]);
            Assert.Equal("action=Start", (string?)entry["detail"]);
        });
        Assert.Equal(202, (int?)entries[1]["status"]);
    }

    [Fact]
    public async Task RejectedAction_WritesFailedWithTheResponseStatus()
    {
        using var host = new TestHost();
        host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Off));
        host.Pair(_certificate);
        using var client = host.CreateClient(_certificate);

        var response = await client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "shutdown" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var failed = host.AuditEntries()[^1];
        Assert.Equal("failed", (string?)failed["outcome"]);
        Assert.Equal(409, (int?)failed["status"]);
        Assert.Contains("not valid", (string?)failed["detail"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditLogUnavailable_Returns503_AndTheActionDoesNotRun()
    {
        using var host = new TestHost(services => services.AddSingleton<IAuditLog>(new FailingAuditLog()));
        host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Off));
        host.Pair(_certificate);
        using var client = host.CreateClient(_certificate);

        var response = await client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "start" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(host.Invoker.Calls);
    }

    [Fact]
    public async Task HyperVUnavailable_StillAuditsTheAttempt()
    {
        using var host = new TestHost();
        host.Inventory.ThrowOnRead = new HyperVUnavailableException("Hyper-V is not enabled.");
        host.Pair(_certificate);
        using var client = host.CreateClient(_certificate);

        var response = await client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "start" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var entries = host.AuditEntries();
        Assert.Equal(["requested", "failed"], entries.Select(entry => (string?)entry["outcome"]));
        Assert.Equal(VmId.ToString(), (string?)entries[0]["vmId"]);
        Assert.False(entries[0].ContainsKey("vmName"));
        Assert.Equal(503, (int?)entries[1]["status"]);
    }

    [Fact]
    public async Task Unpair_IsAudited()
    {
        using var host = new TestHost();
        var device = host.Pair(_certificate, "Phone");
        using var client = host.CreateClient(_certificate);

        var response = await client.DeleteAsync("/api/v1/pairing/devices/self");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var succeeded = host.AuditEntries()[^1];
        Assert.Equal("unpairSelf", (string?)succeeded["action"]);
        Assert.Equal("succeeded", (string?)succeeded["outcome"]);
        Assert.Equal(device.DeviceId.ToString(), (string?)succeeded["deviceId"]);
        Assert.Equal("Phone", (string?)succeeded["deviceName"]);
    }

    [Fact]
    public async Task ReadOnlyRequests_AreNotAudited()
    {
        using var host = new TestHost();
        host.Pair(_certificate);
        using var client = host.CreateClient(_certificate);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/vms")).StatusCode);

        Assert.Empty(host.AuditEntries());
    }

    private sealed class FailingAuditLog : IAuditLog
    {
        public void Write(AuditEntry entry) => throw new AuditUnavailableException(new IOException("The disk is full."));
    }
}
