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
    [InlineData("WakeCheckStatus", typeof(WakeCheckStatus))]
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

    private static YamlMappingNode LoadSchemas()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "api.yaml");
        using var reader = new StreamReader(path);
        var stream = new YamlStream();
        stream.Load(reader);

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var components = (YamlMappingNode)root["components"];
        return (YamlMappingNode)components["schemas"];
    }
}
