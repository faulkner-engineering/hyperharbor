using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Auth;
using HyperHarbor.Shared.Contracts.Hosts;
using HyperHarbor.Shared.Contracts.Pairing;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using HyperHarbor.Shared.Contracts.Wake;
using YamlDotNet.RepresentationModel;

namespace HyperHarbor.Host.Tests.Contracts;

/// <summary>
/// One sample of every wire DTO, serialized exactly as the host sends it, in ContractFixtures.json.
/// This test checks each sample against its api.yaml schema (no unknown property, every required
/// one present, enums and nested objects included). The Rust client parses the same file with its
/// own types, so a rename on either side fails a test instead of a request.
/// Set HH_WRITE_CONTRACT_FIXTURES=1 to regenerate the file after an intentional contract change.
/// </summary>
public class ContractFixtureTests
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly VmComputeSettings ComputeSample = new(
        VmId, VmState.Running, 4, 4096, 4096, false, true, true, 1,
        [ComputeSetting.ProcessorCount, ComputeSetting.StartupMemoryMb, ComputeSetting.MaximumMemoryMb, ComputeSetting.DynamicMemory, ComputeSetting.NestedVirtualization]);

    private static Dictionary<string, object> Samples() => new()
    {
        ["HostInfo"] = new HostInfo(Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f"), "TC-PC", "0.1.0", ContractInfo.ApiVersion, new string('A', 64)),
        ["Vm"] = new Vm(
            VmId,
            "Ubuntu Dev",
            VmState.Running,
            3600,
            12,
            2048,
            2,
            true,
            ["192.168.0.50", "fe80::5"],
            Provisioned: true,
            RemoteDesktop: new VmRemoteDesktop("192.168.0.50", true),
            GuestOs: new VmGuestOs(GuestOsFamily.Linux, "Ubuntu")),
        // Nulls on purpose: these properties are required but nullable, so null must still be sent.
        ["VmRemoteDesktop"] = new VmRemoteDesktop(null, false),
        ["VmGuestOs"] = new VmGuestOs(GuestOsFamily.Unknown, null),
        ["VmActionRequest"] = new VmActionRequest(VmAction.TurnOff),
        ["VmActionResult"] = new VmActionResult(VmId, VmAction.Shutdown, true, VmState.Running),
        ["ProvisionVmRequest"] = new ProvisionVmRequest("hhadmin", "Fixture-Admin1!", true, true, true),
        ["VmProvisioning"] = new VmProvisioning(VmId, "hh-owner", Time),
        ["VmConnection"] = new VmConnection(@".\hh-owner", "Fixture-Pass1!", "192.168.0.50", 3389, Time, GuestOsFamily.Windows),
        ["ConsoleSession"] = new ConsoleSession("fixture-ticket", @"HOSTPC\hhc-owner", "Fixture-Console1!", VmId.ToString("D"), Time, Time.AddMinutes(1)),
        ["PairingRequest"] = new PairingRequest("Laptop", "-----BEGIN CERTIFICATE-----\n-----END CERTIFICATE-----\n"),
        ["PairingRequestCreated"] = new PairingRequestCreated(Guid.Parse("3f2a9c1d-4b5e-4f70-8192-a3b4c5d6e7f8"), [1, 2, 3], Time),
        ["PairingConfirmation"] = new PairingConfirmation([4, 5, 6], [7, 8, 9]),
        ["PairingResult"] = new PairingResult(
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa"),
            Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f"),
            "-----BEGIN CERTIFICATE-----\n-----END CERTIFICATE-----\n",
            [10, 11, 12]),
        ["WakeAdapter"] = new WakeAdapter("Ethernet", "00155D012345", "192.168.0.10", "192.168.0.255"),
        ["WakeInfo"] = new WakeInfo([new WakeAdapter("Ethernet", "00155D012345", "192.168.0.10", "192.168.0.255")]),
        ["WakeCheck"] = new WakeCheck("adapterLink", "Network link", WakeCheckStatus.Pass, null, false),
        ["WakeReadiness"] = new WakeReadiness(false, [new WakeCheck("magicPacket", "Wake on magic packet", WakeCheckStatus.Fail, "Off", true)]),
        ["WakeFixRequest"] = new WakeFixRequest(["magicPacket"]),
        ["WakeTestRequest"] = new WakeTestRequest(30),
        ["WakeTestScheduled"] = new WakeTestScheduled(Time),
        ["ElevateRequest"] = new ElevateRequest("Fixture passphrase"),
        ["ElevationGrant"] = new ElevationGrant("Zml4dHVyZS10b2tlbi1maXh0dXJlLXRva2VuLWZpeHR1cg", Time),
        // Null on purpose: expiresAt is required but nullable.
        ["ElevationStatus"] = new ElevationStatus(true, false, null),
        ["VmComputeSettings"] = ComputeSample,
        ["UpdateVmComputeRequest"] = new UpdateVmComputeRequest(4, null, null, false, true, true, ShutDownToApply: true),
        ["VmComputeUpdate"] = new VmComputeUpdate(ComputeSample, null),
        ["CreateVmRequest"] = new CreateVmRequest("Win11 Dev", "Win11_24H2.iso", 64, 4, 4096, 8192, true, "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444", true, false,
            new UnattendedInstallRequest("windows-workstation", "Windows 11 Pro", "WIN11-DEV")),
        ["UnattendedInstallRequest"] = new UnattendedInstallRequest("ubuntu-dev-server"),
        ["UnattendProfile"] = new UnattendProfile("windows-burner", "Windows Burner", true, InstallOs.Windows, "hhadmin", "Central Standard Time", "en-US",
            new WindowsInstallSettings("Windows 11 Pro", BypassHardwareChecks: true), null),
        ["UnattendProfileRequest"] = new UnattendProfileRequest("Build Server", InstallOs.Linux, "builder", "America/Chicago", "en-US", null,
            new LinuxInstallSettings(["ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFixture user@laptop"], ["git"], InstallDesktop: false)),
        ["WindowsInstallSettings"] = new WindowsInstallSettings("Windows 11 Pro", false, true, true, false, true),
        ["LinuxInstallSettings"] = new LinuxInstallSettings([], ["git", "curl"], true),
        ["UnattendedInstallStatus"] = new UnattendedInstallStatus(VmId, "windows-burner", InstallOs.Windows, UnattendedInstallState.WaitingForRemoteAccess,
            "Waiting for Remote Desktop to answer", Time, Time.AddMinutes(12), null),
        ["IsoInspection"] = new IsoInspection(InstallOs.Windows, "Windows", ["Windows 11 Home", "Windows 11 Pro"]),
        ["ValidationIssue"] = new ValidationIssue("processorCount", "Use at most 16 virtual processors."),
        ["VmDeleteRequest"] = new VmDeleteRequest(true, true, "Ubuntu Dev"),
        ["DeleteBlocker"] = new DeleteBlocker(DeleteBlockerCode.SharedDisk, DeleteBlockerScope.DeleteDisksOrCheckpoints, @"C:\VMs\Base.vhdx is also used by Web."),
        ["VmDeletePreview"] = new VmDeletePreview(
            VmId,
            "Ubuntu Dev",
            VmState.Off,
            1,
            [@"C:\VMs\Ubuntu Dev.vhdx"],
            [new DeleteBlocker(DeleteBlockerCode.DiskNotDeletable, DeleteBlockerScope.DeleteDisks, "In use.")]),
        ["JobError"] = new JobError("Deleting disks failed", "The file is in use."),
        ["HostResources"] = new HostResources(16, 32768, 20000, 4096, @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks", @"C:\Users\Public\Documents\HyperHarbor ISOs"),
        ["IsoImage"] = new IsoImage("Win11_24H2.iso", 5_800_000_000, Time, ["Win11 Dev"]),
        ["RenameIsoRequest"] = new RenameIsoRequest("Windows 11 24H2.iso"),
        ["VirtualSwitch"] = new VirtualSwitch("C08CB7B8-9B3C-408E-8E30-5E16A3AEB444", "Default Switch", true),

        // Nulls on purpose: vmId and error are required but nullable.
        ["VmJob"] = new VmJob(Guid.Parse("7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d"), VmJobKind.CreateVm, null, VmJobState.Running, "Creating the disk", 10, Time, Time, null),
    };

    [Fact]
    public void Fixtures_AreCurrent()
    {
        var path = FixturesPath();
        var computed = Serialize();

        if (Environment.GetEnvironmentVariable("HH_WRITE_CONTRACT_FIXTURES") == "1")
        {
            File.WriteAllText(path, computed);
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), computed);
    }

    public static TheoryData<string> SchemaNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Samples().Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SchemaNames))]
    public void Fixture_MatchesItsSchema(string schemaName)
    {
        var sample = JsonSerializer.SerializeToNode(Samples()[schemaName], Samples()[schemaName].GetType(), ContractJson.Options)!;

        var problems = new List<string>();
        Validate(sample, Schemas()[schemaName], schemaName, problems);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EveryContractSchema_HasAFixture()
    {
        var objectSchemas = Schemas().Children
            .Where(pair => pair.Value is YamlMappingNode schema && schema.Children.ContainsKey("properties"))
            .Select(pair => ((YamlScalarNode)pair.Key).Value!)
            .Where(name => name != "ProblemDetails")
            .Order();

        Assert.Equal(objectSchemas, Samples().Keys.Order());
    }

    private static string Serialize()
    {
        var root = new JsonObject();
        foreach (var (name, sample) in Samples().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            root[name] = JsonSerializer.SerializeToNode(sample, sample.GetType(), ContractJson.Options);
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    private static void Validate(JsonNode? node, YamlNode schemaNode, string at, List<string> problems)
    {
        var schema = Resolve((YamlMappingNode)schemaNode);

        // oneOf is used only for "this object or null"; validate against the object branch.
        if (schema.Children.TryGetValue(new YamlScalarNode("oneOf"), out var oneOf))
        {
            var branches = ((YamlSequenceNode)oneOf).Children.Cast<YamlMappingNode>().ToList();
            var nullable = branches.Any(branch => branch.Children.TryGetValue(new YamlScalarNode("type"), out var type) && ((YamlScalarNode)type).Value == "null");
            if (node is null)
            {
                if (!nullable)
                {
                    problems.Add($"{at} is null but the schema does not allow null.");
                }

                return;
            }

            Validate(node, branches.First(branch => !branch.Children.ContainsKey(new YamlScalarNode("type")) || ((YamlScalarNode)branch["type"]).Value != "null"), at, problems);
            return;
        }

        if (node is null)
        {
            if (!AllowsNull(schema))
            {
                problems.Add($"{at} is null but the schema does not allow null.");
            }

            return;
        }

        if (schema.Children.TryGetValue(new YamlScalarNode("enum"), out var values))
        {
            var allowed = ((YamlSequenceNode)values).Children.Select(value => ((YamlScalarNode)value).Value);
            var actual = node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
            if (!allowed.Contains(actual))
            {
                problems.Add($"{at} = {node} is not one of [{string.Join(", ", allowed)}].");
            }
        }

        if (schema.Children.TryGetValue(new YamlScalarNode("properties"), out var propertiesNode))
        {
            var properties = (YamlMappingNode)propertiesNode;
            var json = node.AsObject();
            foreach (var (name, value) in json)
            {
                if (!properties.Children.TryGetValue(new YamlScalarNode(name), out var propertySchema))
                {
                    problems.Add($"{at}.{name} is not in the schema.");
                    continue;
                }

                Validate(value, propertySchema, $"{at}.{name}", problems);
            }

            if (schema.Children.TryGetValue(new YamlScalarNode("required"), out var required))
            {
                foreach (var name in ((YamlSequenceNode)required).Children.Select(item => ((YamlScalarNode)item).Value!))
                {
                    if (!json.ContainsKey(name))
                    {
                        problems.Add($"{at}.{name} is required but missing.");
                    }
                }
            }
        }

        if (schema.Children.TryGetValue(new YamlScalarNode("items"), out var items) && node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                Validate(array[index], items, $"{at}[{index}]", problems);
            }
        }
    }

    private static YamlMappingNode Resolve(YamlMappingNode schema)
    {
        if (schema.Children.TryGetValue(new YamlScalarNode("$ref"), out var reference))
        {
            var name = ((YamlScalarNode)reference).Value!.Split('/')[^1];
            return Resolve((YamlMappingNode)Schemas()[name]);
        }

        return schema;
    }

    private static bool AllowsNull(YamlMappingNode schema) =>
        schema.Children.TryGetValue(new YamlScalarNode("type"), out var type)
        && type is YamlSequenceNode types
        && types.Children.Any(item => ((YamlScalarNode)item).Value == "null");

    private static YamlMappingNode Schemas()
    {
        using var reader = new StreamReader(Path.Combine(AppContext.BaseDirectory, "api.yaml"));
        var stream = new YamlStream();
        stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return (YamlMappingNode)((YamlMappingNode)root["components"])["schemas"];
    }

    private static string FixturesPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "ContractFixtures.json");
}
