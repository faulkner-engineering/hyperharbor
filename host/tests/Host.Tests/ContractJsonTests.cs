using System.Text.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Pairing;
using HyperHarbor.Shared.Contracts.Vms;
using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Tests;

public class ContractJsonTests
{
    [Fact]
    public void VmActionResult_SerializesWithCamelCaseNamesAndEnumValues()
    {
        var result = new VmActionResult(
            Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b"),
            VmAction.TurnOff,
            Accepted: true,
            VmState.Stopping);

        var json = JsonSerializer.SerializeToNode(result, ContractJson.Options)!.AsObject();

        Assert.Equal("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b", (string?)json["vmId"]);
        Assert.Equal("turnOff", (string?)json["action"]);
        Assert.True((bool?)json["accepted"]);
        Assert.Equal("stopping", (string?)json["state"]);
    }

    [Fact]
    public void VmActionRequest_DeserializesFromContractExample()
    {
        var request = JsonSerializer.Deserialize<VmActionRequest>("""{"action":"start"}""", ContractJson.Options);

        Assert.Equal(VmAction.Start, request!.Action);
    }

    [Fact]
    public void VmActionRequest_RejectsIntegerEnumValues()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<VmActionRequest>("""{"action":2}""", ContractJson.Options));
    }

    [Fact]
    public void VmActionRequest_RejectsMissingAction()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<VmActionRequest>("{}", ContractJson.Options));
    }

    [Fact]
    public void Vm_RoundTripsContractExample()
    {
        const string example = """
            {
              "id": "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
              "name": "Dev Workstation",
              "state": "running",
              "uptimeSeconds": 3600,
              "cpuUsagePercent": 12,
              "memoryAssignedMb": 8192,
              "generation": 2,
              "rdpAvailable": true,
              "ipAddresses": ["192.168.1.50"]
            }
            """;

        var vm = JsonSerializer.Deserialize<Vm>(example, ContractJson.Options)!;
        var roundTrip = JsonSerializer.SerializeToNode(vm, ContractJson.Options);

        Assert.Equal(VmState.Running, vm.State);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(example), roundTrip));
    }

    [Fact]
    public void Vm_OmitsNullOptionalProperties()
    {
        var vm = new Vm(Guid.NewGuid(), "Off VM", VmState.Off, null, null, null, 2, false, []);

        var json = JsonSerializer.SerializeToNode(vm, ContractJson.Options)!.AsObject();

        Assert.False(json.ContainsKey("uptimeSeconds"));
        Assert.False(json.ContainsKey("cpuUsagePercent"));
        Assert.False(json.ContainsKey("memoryAssignedMb"));
    }

    [Fact]
    public void PairingConfirmation_EncodesProofAsBase64()
    {
        var confirmation = new PairingConfirmation([0x01, 0x02, 0xFF]);

        var json = JsonSerializer.SerializeToNode(confirmation, ContractJson.Options)!.AsObject();

        Assert.Equal("AQL/", (string?)json["proof"]);
    }

    [Fact]
    public void WakeReadiness_SerializesCheckStatusAsString()
    {
        var readiness = new WakeReadiness(
            Ready: false,
            [new WakeCheck("nicWakeOnMagicPacket", "Network adapter wakes on magic packet", WakeCheckStatus.Fail, null, true)]);

        var json = JsonSerializer.SerializeToNode(readiness, ContractJson.Options)!.AsObject();

        Assert.Equal("fail", (string?)json["checks"]![0]!["status"]);
        Assert.Equal("nicWakeOnMagicPacket", (string?)json["checks"]![0]!["id"]);
    }
}
