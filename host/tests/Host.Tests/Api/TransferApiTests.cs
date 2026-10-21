using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Host.Tests.Transfer;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

/// <summary>
/// The Transfers routes. One test host plays both hosts: a copy of its own VM goes through the real transfer job, the real
/// HTTP channel, and the real ticket routes (in memory), with Hyper-V faked.
/// </summary>
public sealed class TransferApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _device = TestHost.CreateClientCertificate("Laptop");
    private readonly X509Certificate2 _sourceHost = TestHost.CreateClientCertificate("SOURCE-HOST");
    private readonly HttpClient _client;
    private readonly byte[] _diskBytes = RandomNumberGenerator.GetBytes(10_000);
    private readonly string _disk;

    public TransferApiTests()
    {
        // Never the fake Hyper-V default folder (C:\Hyper-V\Config): a received VM's folder is really created.
        _host.Services.GetRequiredService<HostSettingsStore>().SetVmFolder(Path.Combine(_host.DataDirectory, "vms"));

        _disk = Path.Combine(_host.DataDirectory, "source", "Web.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(_disk)!);
        File.WriteAllBytes(_disk, _diskBytes);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Web", VmState.Off));
        _host.Storage.Attach(VmId, "Web", _disk);
        _host.DiskFiles.Files.Add(_disk);
        _host.DiskFiles.Sizes[_disk] = _diskBytes.Length;
        _host.Definitions.Current = () => new VmDefinition(
            VmId, "Web", 2, null, new ComputeState(2, 2048, 2048, false, false, false, 1), true, CimHyperVBuilder.MicrosoftWindowsTemplateId, true,
            [new VmAdapterDefinition(CimHyperVHost.DefaultSwitchId, "Default Switch", "00155D010203")],
            [new VmDiskDefinition(_disk, "scsi", 0, 0)], null, 128, 512);

        _host.Pair(_device, "Laptop");
        _client = _host.CreateClient(_device);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
    }

    private string SourceFingerprint => CertificateFingerprint.Of(_sourceHost);

    public void Dispose()
    {
        _client.Dispose();
        _device.Dispose();
        _sourceHost.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Preview_DescribesTheVm()
    {
        var preview = await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/transfer-preview");

        Assert.Equal("Web", (string?)preview!["name"]);
        Assert.Equal(_diskBytes.Length, (long)preview["totalBytes"]!);
        Assert.True((bool)preview["tpmEnabled"]!);
        Assert.Equal("12.0", (string?)preview["configurationVersion"]);
        Assert.Empty(preview["blockers"]!.AsArray());
    }

    [Fact]
    public async Task Preview_OfARunningVm_HasABlocker()
    {
        _host.Inventory.SetState(VmId, VmState.Running);

        var preview = await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/transfer-preview");

        Assert.Equal("notOff", (string?)preview!["blockers"]![0]!["code"]);
    }

    [Fact]
    public async Task Offer_NeedsElevation()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/transfers/incoming", await IncomingRequestAsync("copy"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(_host.Services.GetRequiredService<TransferReceiver>().AnyActive);
    }

    [Fact]
    public async Task Offer_OfAMoveOfAVmThisHostHas_IsRefused()
    {
        var response = await SendElevatedAsync(HttpMethod.Post, "/api/v1/transfers/incoming", await IncomingRequestAsync("move"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("vmId", (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["field"]);
    }

    [Fact]
    public async Task Offer_WithUnacknowledgedWarnings_IsAConflict()
    {
        var request = await IncomingRequestAsync("copy");
        request["acknowledgeWarnings"] = false;

        var response = await SendElevatedAsync(HttpMethod.Post, "/api/v1/transfers/incoming", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.ResourceWarnings, (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
    }

    [Fact]
    public async Task Offer_ReturnsATicket_TheGuardian_AndMakesTheHostBusy()
    {
        // A key that the (sending) host owns and this host does not hold, so this host asks for a grant.
        var request = await IncomingRequestAsync("copy");
        request["preview"]!["tpmKeyOwner"] = "BBBB";
        request["preview"]!["tpmOwnedBySource"] = true;
        var response = await SendElevatedAsync(HttpMethod.Post, "/api/v1/transfers/incoming", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var offer = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(43, ((string)offer["ticket"]!).Length);
        Assert.Equal("Web (copy)", (string?)offer["name"]);
        Assert.Null(offer["vmId"]);
        Assert.Equal(Convert.ToBase64String(_host.KeyProtectors.LocalGuardianExport), (string?)offer["guardian"]);
        Assert.True(_host.Services.GetRequiredService<HostActivity>().InProgress);
    }

    [Fact]
    public async Task TicketRoutes_TakeTheTicketWithTheSourceCertificate_AndNothingElse()
    {
        var offer = await OfferAsync();
        var id = (string)offer["transferId"]!;
        var ticket = (string)offer["ticket"]!;

        using var source = TicketClient(_sourceHost, ticket);
        Assert.Equal(HttpStatusCode.OK, (await source.GetAsync($"/api/v1/transfers/{id}")).StatusCode);

        // The paired device with the right ticket, the source without a ticket, a wrong ticket, another transfer's path.
        using var device = TicketClient(_device, ticket);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync($"/api/v1/transfers/{id}")).StatusCode);
        using var bare = _host.CreateClient(_sourceHost);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bare.GetAsync($"/api/v1/transfers/{id}")).StatusCode);
        using var wrong = TicketClient(_sourceHost, ticket + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync($"/api/v1/transfers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await source.GetAsync($"/api/v1/transfers/{Guid.NewGuid()}")).StatusCode);

        // A ticket opens no other route.
        Assert.Equal(HttpStatusCode.Unauthorized, (await source.GetAsync("/api/v1/vms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await source.GetAsync($"/api/v1/vms/{VmId}/transfer-preview")).StatusCode);

        // And the paired device's own routes do not take the ticket scheme in its place.
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/v1/vms")).StatusCode);
    }

    [Fact]
    public async Task Blocks_NeedARangeAndADigest_AndStartWhereTheFileEnds()
    {
        var offer = await OfferAsync();
        var id = (string)offer["transferId"]!;
        using var source = TicketClient(_sourceHost, (string)offer["ticket"]!);
        var path = $"/api/v1/transfers/{id}/files/{Uri.EscapeDataString("disk:0")}";
        var block = _diskBytes;

        var noRange = await source.PutAsync(path, new ByteArrayContent(block));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, noRange.StatusCode);

        var noDigest = await source.PutAsync(path, Block(block, 0, block.Length, digest: null));
        Assert.Equal(HttpStatusCode.BadRequest, noDigest.StatusCode);

        var badHash = await source.PutAsync(path, Block(block, 0, block.Length, SHA256.HashData("other"u8)));
        Assert.Equal(HttpStatusCode.Conflict, badHash.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.TransferFailed, (string?)(await badHash.Content.ReadFromJsonAsync<JsonObject>())!["code"]);

        var wrongOffset = await source.PutAsync(path, Block(block[..100], 100, block.Length, SHA256.HashData(block[..100])));
        Assert.Equal(HttpStatusCode.Conflict, wrongOffset.StatusCode);
        var problem = (await wrongOffset.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(ContractInfo.ProblemCodes.TransferOffset, (string?)problem["code"]);
        Assert.Equal(0, (long)problem["expectedOffset"]!);

        var good = await source.PutAsync(path, Block(block, 0, block.Length, SHA256.HashData(block)));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(block.Length, (long)(await good.Content.ReadFromJsonAsync<JsonObject>())!["received"]!);
    }

    [Fact]
    public async Task Copy_ToItself_RunsTheWholeTransfer_OverTheTicketRoutes()
    {
        const string adminPassword = "Admin-Secret1!";
        var owner = _host.Services.GetRequiredService<Core.Users.UserStore>().GetOrCreateDefault();
        var accounts = _host.Services.GetRequiredService<Core.Provisioning.ProvisioningStore>();
        var credentials = _host.Services.GetRequiredService<Core.Provisioning.VmCredentialStore>();
        var gameAware = _host.Services.GetRequiredService<Core.GameAware.GameAwareStore>();
        accounts.Save(new Core.Provisioning.ProvisionedAccount(VmId, owner.UserId, owner.VmAccountName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        credentials.Save(VmId, new Core.Provisioning.GuestCredential("hhadmin", adminPassword));
        gameAware.Update(settings => settings with
        {
            VmPolicies = new Dictionary<string, Core.GameAware.GameVmPolicy> { [VmId.ToString("D")] = new(Core.GameAware.GamePolicy.Keep) },
        });

        var offer = await OfferAsync();
        var ticket = (string)offer["ticket"]!;
        UseThisHostAsTheReceiver(ticket);

        var started = await SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{VmId}/transfer", StartRequest(offer, "copy"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var job = await WaitForJobAsync((string)(await started.Content.ReadFromJsonAsync<JsonObject>())!["id"]!);

        Assert.Equal("succeeded", (string?)job["state"]);
        Assert.Equal("transferVm", (string?)job["kind"]);
        var result = job["transferResult"]!;
        Assert.Equal("Web (copy)", (string?)result["targetName"]);
        Assert.False((bool)result["sourceRemoved"]!);
        Assert.NotEqual(VmId, Guid.Parse((string)result["targetVmId"]!));
        Assert.Equal(_diskBytes, await File.ReadAllBytesAsync(Path.Combine(_host.DataDirectory, "vms", "Web (copy)", "Virtual Hard Disks", "Web.vhdx")));
        Assert.Contains("startup 3", _host.TransferHyperV.Log);
        Assert.Equal(FakeKeyProtectorAccess.Original, _host.KeyProtectors.Current);
        Assert.False(_host.Services.GetRequiredService<TransferReceiver>().AnyActive);
        Assert.Empty(_host.Storage.DeletedVms);

        // The records went with it, under the copy's new ID.
        var copyId = Guid.Parse((string)result["targetVmId"]!);
        Assert.NotNull(accounts.Find(copyId, owner.UserId));
        Assert.Equal(adminPassword, credentials.Find(copyId)!.Password);
        Assert.Equal(Core.GameAware.GamePolicy.Keep, gameAware.Load().VmPolicies[copyId.ToString("D")].Policy);
        Assert.NotNull(accounts.Find(VmId, owner.UserId));
        _host.Logs.AssertNoneContain(adminPassword);
        Assert.All(_host.AuditEntries(), entry => Assert.DoesNotContain(adminPassword, entry.ToJsonString(), StringComparison.Ordinal));

        // The sending host is audited as a peer; the ticket is in no log or audit entry.
        var blocks = _host.AuditEntries().Where(entry => (string?)entry["action"] == "receiveTransferBlock").ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, entry => Assert.StartsWith(TransferTicketAuthenticationHandler.PeerName, (string?)entry["deviceName"]));
        _host.Logs.AssertNoneContain(ticket);
        Assert.All(_host.AuditEntries(), entry => Assert.DoesNotContain(ticket, entry.ToJsonString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_NeedsTheVmsName()
    {
        var offer = await OfferAsync();
        var request = StartRequest(offer, "move");
        request["confirmName"] = "web";

        var response = await SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{VmId}/transfer", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("confirmName", (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["field"]);
    }

    [Fact]
    public async Task Start_NeedsElevation()
    {
        var offer = await OfferAsync();

        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/transfer", StartRequest(offer, "copy"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.PeerChannels.Requests);
    }

    [Fact]
    public async Task Cancel_StopsARunningTransfer_AndCancelsItOnTheReceiver()
    {
        var offer = await OfferAsync();
        var waiting = new TaskCompletionSource();
        var channel = new HangingChannel(waiting);
        _host.PeerChannels.OpenChannel = _ => channel;

        var started = await SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{VmId}/transfer", StartRequest(offer, "copy"));
        var jobId = (string)(await started.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancel = await _client.PostAsync($"/api/v1/vms/{VmId}/transfer/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        var job = await WaitForJobAsync(jobId);

        Assert.Equal("failed", (string?)job["state"]);
        Assert.Equal(VmJobStore.CanceledDetail, (string?)job["error"]!["detail"]);
        Assert.True(channel.Canceled);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/v1/vms/{VmId}/transfer/cancel", null)).StatusCode);
    }

    /// <summary>The transfer job's channel goes through the real HTTP client code to this test host's ticket routes.</summary>
    private void UseThisHostAsTheReceiver(string ticket) =>
        _host.PeerChannels.OpenChannel = request => new HttpPeerTransferChannel(TicketClient(_sourceHost, ticket), request);

    private HttpClient TicketClient(X509Certificate2 certificate, string ticket)
    {
        var client = _host.CreateClient(certificate);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ContractInfo.TransferTicketScheme, ticket);
        return client;
    }

    private static ByteArrayContent Block(byte[] bytes, long offset, long fileSize, byte[]? digest)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + bytes.Length - 1, fileSize);
        if (digest is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Digest", $"sha-256=:{Convert.ToBase64String(digest)}:");
        }

        return content;
    }

    private async Task<JsonObject> IncomingRequestAsync(string mode)
    {
        var preview = await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/transfer-preview");
        return new JsonObject
        {
            ["preview"] = preview,
            ["mode"] = mode,
            ["sourceFingerprint"] = SourceFingerprint,
            ["acknowledgeWarnings"] = true,
        };
    }

    private async Task<JsonObject> OfferAsync()
    {
        var response = await SendElevatedAsync(HttpMethod.Post, "/api/v1/transfers/incoming", await IncomingRequestAsync("copy"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static JsonObject StartRequest(JsonObject offer, string mode) => new()
    {
        ["mode"] = mode,
        ["target"] = new JsonObject
        {
            ["hostId"] = Guid.NewGuid().ToString(),
            ["name"] = "SELF",
            ["addresses"] = new JsonArray("localhost"),
            ["port"] = 443,
            ["fingerprint"] = new string('C', 64),
        },
        ["transferId"] = offer["transferId"]!.DeepClone(),
        ["ticket"] = offer["ticket"]!.DeepClone(),
        ["guardian"] = offer["guardian"]?.DeepClone(),
        ["confirmName"] = "Web",
    };

    private async Task<HttpResponseMessage> SendElevatedAsync(HttpMethod method, string path, JsonObject body)
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }

    private async Task<JsonObject> WaitForJobAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var job = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{jobId}"))!;
            if ((string?)job["state"] != "running" || DateTime.UtcNow > deadline)
            {
                return job;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Waits in its first call until the job is canceled; records the cancel of the receiver.</summary>
    private sealed class HangingChannel(TaskCompletionSource waiting) : IPeerTransferChannel
    {
        public bool Canceled { get; private set; }

        public async Task<PeerTransferStatus> StatusAsync(CancellationToken cancellationToken)
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Not reached.");
        }

        public Task SendBlockAsync(string fileKey, long offset, long length, long fileSize, byte[] sha256, Stream block, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task<TransferImportResult> ImportAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Not reached.");

        public Task SendRecordsAsync(TransferRecords records, CancellationToken cancellationToken) => throw new InvalidOperationException("Not reached.");

        public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            Canceled = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
