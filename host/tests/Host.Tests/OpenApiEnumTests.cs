using System.Text.Json;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using HyperHarbor.Shared.Contracts.Wake;
using YamlDotNet.RepresentationModel;

namespace HyperHarbor.Host.Tests;

/// <summary>
/// Guards against drift between the C# enumerations and the enum lists in docs/api.yaml.
/// </summary>
public class OpenApiEnumTests
{
    private static readonly YamlMappingNode Schemas = LoadSchemas();

    [Theory]
    [InlineData("VmState", typeof(VmState))]
    [InlineData("VmAction", typeof(VmAction))]
    [InlineData("GuestOsFamily", typeof(GuestOsFamily))]
    [InlineData("WakeCheckStatus", typeof(WakeCheckStatus))]
    [InlineData("DeleteBlockerCode", typeof(DeleteBlockerCode))]
    [InlineData("DeleteBlockerScope", typeof(DeleteBlockerScope))]
    [InlineData("VmJobKind", typeof(VmJobKind))]
    [InlineData("VmJobState", typeof(VmJobState))]
    [InlineData("ComputeSetting", typeof(ComputeSetting))]
    [InlineData("InstallOs", typeof(Shared.Contracts.Unattend.InstallOs))]
    [InlineData("UnattendedInstallState", typeof(Shared.Contracts.Unattend.UnattendedInstallState))]
    public void EnumWireValues_MatchOpenApiSchema(string schemaName, Type enumType)
    {
        var schema = (YamlMappingNode)Schemas[schemaName];
        var expected = ((YamlSequenceNode)schema["enum"]).Children
            .Select(node => ((YamlScalarNode)node).Value!)
            .ToArray();

        var actual = Enum.GetValues(enumType)
            .Cast<object>()
            .Select(value => JsonSerializer.Serialize(value, enumType, ContractJson.Options).Trim('"'))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApiVersion_MatchesOpenApiInfoVersion()
    {
        var info = (YamlMappingNode)LoadRoot()["info"];

        Assert.Equal(ContractInfo.ApiVersion, ((YamlScalarNode)info["version"]).Value);
    }

    private static YamlMappingNode LoadSchemas()
    {
        var components = (YamlMappingNode)LoadRoot()["components"];
        return (YamlMappingNode)components["schemas"];
    }

    private static YamlMappingNode LoadRoot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "api.yaml");
        using var reader = new StreamReader(path);
        var stream = new YamlStream();
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }
}
