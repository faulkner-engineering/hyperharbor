using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// Machine profile files: the header, the machine sections as plain YAML mappings, then the software sections
/// exactly as a setup profile writes them (ids with their names as comments). The machine sections go through
/// the contract's JSON shape, so a file and the API agree on every field name and type.
/// </summary>
public static class MachineProfileYaml
{
    public const int SchemaVersion = 1;
    public const string SchemaUrl = "https://raw.githubusercontent.com/faulkner-engineering/hyperharbor/main/schemas/machine-profile.v1.schema.json";

    private static readonly string[] TopLevel =
    [
        "schemaVersion", "name", "description", "os", "account", "display", "firmware", "compute", "gpu", "network", "backup",
        "install", "remove", "tweaks", "browser",
    ];

    /// <summary>Contract JSON that refuses unknown fields, so a misspelled key in a file is reported instead of ignored.</summary>
    private static readonly JsonSerializerOptions Strict = new(ContractJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Write(MachineProfile profile, IReadOnlyDictionary<string, string>? policyTypes = null)
    {
        var lines = new List<string>
        {
            $"# yaml-language-server: $schema={SchemaUrl}",
            $"schemaVersion: {SchemaVersion}",
            $"name: {ProfileYamlWriter.Scalar(profile.Name)}",
        };
        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            lines.Add($"description: {ProfileYamlWriter.Scalar(profile.Description)}");
        }

        lines.Add($"os: {(profile.Os == InstallOs.Linux ? "linux" : "windows")}");
        Section(lines, "account", profile.Account);
        Section(lines, "display", profile.Display);
        Section(lines, "firmware", profile.Firmware);
        Section(lines, "compute", profile.Compute);
        Section(lines, "gpu", profile.Gpu);
        Section(lines, "network", profile.Network);
        Section(lines, "backup", profile.Backup);
        ProfileYamlWriter.AppendBody(lines, Software(profile), policyTypes);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The software sections as a setup profile, for the setup profile validator and writer.</summary>
    public static SetupProfile Software(MachineProfile profile) =>
        new(profile.Name, profile.Description, profile.Install, profile.Remove, profile.Tweaks, profile.Browser);

    /// <exception cref="ProfileFormatException">Not YAML, or not a machine profile.</exception>
    public static MachineProfile Read(string yaml)
    {
        var comments = ProfileYamlReader.InlineComments(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new ProfileFormatException($"Line {ex.Start.Line}: {ex.Message}");
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new ProfileFormatException("A machine profile is one YAML document with a mapping at the top.");
        }

        ProfileYamlReader.CheckKeys(root, TopLevel, "a machine profile");
        if (ProfileYamlReader.OptionalScalar(root, "schemaVersion") != SchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new ProfileFormatException($"schemaVersion must be {SchemaVersion}.");
        }

        var name = ProfileYamlReader.OptionalScalar(root, "name") ?? throw new ProfileFormatException("name is required.");
        var os = ProfileYamlReader.OptionalScalar(root, "os") switch
        {
            "windows" => InstallOs.Windows,
            "linux" => InstallOs.Linux,
            null => throw new ProfileFormatException("os is required: windows or linux."),
            var other => throw new ProfileFormatException($"os must be windows or linux, not \"{other}\"."),
        };

        return new MachineProfile(
            name,
            os,
            ProfileYamlReader.OptionalScalar(root, "description"),
            Section<MachineAccount>(root, "account"),
            Section<MachineDisplay>(root, "display"),
            Section<MachineFirmware>(root, "firmware"),
            Section<MachineCompute>(root, "compute"),
            Section<MachineGpu>(root, "gpu"),
            Section<MachineNetwork>(root, "network"),
            Section<MachineBackup>(root, "backup"),
            ProfileYamlReader.Items(root, "install", comments),
            ProfileYamlReader.Remove(root, comments),
            ProfileYamlReader.Tweaks(root, comments),
            ProfileYamlReader.Browser(root, comments));
    }

    private static void Section<T>(List<string> lines, string key, T? section)
        where T : class
    {
        if (section is null)
        {
            return;
        }

        var node = JsonSerializer.SerializeToNode(section, ContractJson.Options);
        Emit(lines, key, node, "");
    }

    private static void Emit(List<string> lines, string key, JsonNode? node, string indent)
    {
        switch (node)
        {
            case JsonObject { Count: 0 }:
                lines.Add($"{indent}{key}: {{}}");
                break;
            case JsonObject map:
                lines.Add($"{indent}{key}:");
                foreach (var (childKey, child) in map)
                {
                    Emit(lines, childKey, child, indent + "  ");
                }

                break;
            case JsonArray { Count: 0 }:
                lines.Add($"{indent}{key}: []");
                break;
            case JsonArray list:
                lines.Add($"{indent}{key}:");
                foreach (var item in list)
                {
                    lines.Add($"{indent}  - {ScalarText(item)}");
                }

                break;
            default:
                lines.Add($"{indent}{key}: {ScalarText(node)}");
                break;
        }
    }

    private static string ScalarText(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue<string>(out var text) => ProfileYamlWriter.Scalar(text),
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        JsonValue value => value.ToJsonString(),
        _ => throw new InvalidOperationException("Machine sections hold only scalars and lists of scalars below a mapping."),
    };

    private static T? Section<T>(YamlMappingNode root, string key)
        where T : class
    {
        if (ProfileYamlReader.Child(root, key) is not { } node)
        {
            return null;
        }

        if (node is not YamlMappingNode)
        {
            throw new ProfileFormatException($"Line {node.Start.Line}: {key} must be a mapping.");
        }

        try
        {
            return ToJson(node)!.Deserialize<T>(Strict);
        }
        catch (JsonException ex)
        {
            var path = ex.Path is { Length: > 1 } jsonPath ? key + jsonPath[1..] : key;
            throw new ProfileFormatException($"Line {node.Start.Line}: {path}: {Reason(ex)}");
        }
    }

    private static string Reason(JsonException ex)
    {
        var message = ex.Message;
        if (message.Contains("could not be mapped", StringComparison.Ordinal))
        {
            return "is not a field of this section.";
        }

        if (message.Contains("missing required properties", StringComparison.Ordinal))
        {
            return message[message.IndexOf("missing", StringComparison.Ordinal)..].TrimEnd('.') + ".";
        }

        return "has a value of the wrong kind (a word, a number, true or false, or a list).";
    }

    /// <summary>
    /// The YAML tree as JSON. Unquoted true, false, and numbers become JSON booleans and numbers; null, ~, and an
    /// empty value become null; everything else, and anything quoted, is a string.
    /// </summary>
    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => KeyValuePair.Create(
            pair.Key is YamlScalarNode { Value: { } name } ? name : throw new ProfileFormatException($"Line {pair.Key.Start.Line}: keys are single words."),
            ToJson(pair.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new ProfileFormatException($"Line {node.Start.Line}: anchors and aliases are not supported."),
    };

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style is not ScalarStyle.Plain and not ScalarStyle.Any)
        {
            return JsonValue.Create(text);
        }

        if (text is "" or "~" or "null")
        {
            return null;
        }

        if (text is "true" or "false")
        {
            return JsonValue.Create(text == "true");
        }

        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : JsonValue.Create(text);
    }
}
