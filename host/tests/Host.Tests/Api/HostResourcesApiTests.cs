using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Tests.Api;

public sealed class HostResourcesApiTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public HostResourcesApiTests()
    {
        _host.Pair(_certificate);
        _client = _host.CreateClient(_certificate);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Resources_ReportCapacityReserveAndFolders()
    {
        _host.Capacity.Capacity = new HostCapacity(12, 65536, 30000);

        var resources = await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/resources");

        Assert.Equal(12, (int?)resources!["logicalProcessorCount"]);
        Assert.Equal(65536, (long?)resources["totalMemoryMb"]);
        Assert.Equal(30000, (long?)resources["availableMemoryMb"]);
        Assert.Equal(4096, (long?)resources["memoryReserveMb"]);
        Assert.Equal(@"C:\Hyper-V\Virtual Hard Disks", (string?)resources["virtualHardDiskFolder"]);
        Assert.Equal(_host.IsoFolder, (string?)resources["isoFolder"]);
    }

    [Fact]
    public async Task Isos_ListTheLibrary()
    {
        Directory.CreateDirectory(_host.IsoFolder);
        File.WriteAllBytes(Path.Combine(_host.IsoFolder, "ubuntu-24.04.iso"), new byte[3]);

        var isos = await _client.GetFromJsonAsync<JsonArray>("/api/v1/isos");

        var image = Assert.Single(isos!)!;
        Assert.Equal("ubuntu-24.04.iso", (string?)image["name"]);
        Assert.Equal(3, (long?)image["sizeBytes"]);
    }

    [Fact]
    public async Task Switches_PutTheDefaultSwitchFirst()
    {
        var switches = await _client.GetFromJsonAsync<JsonArray>("/api/v1/switches");

        Assert.Equal(2, switches!.Count);
        Assert.True((bool?)switches[0]!["isDefault"]);
        Assert.Equal("Default Switch", (string?)switches[0]!["name"]);
    }
}
