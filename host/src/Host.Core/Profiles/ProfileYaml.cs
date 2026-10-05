using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HyperHarbor.Shared.Contracts.Profiles;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;
using YamlDotNet.RepresentationModel;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>A profile file that is not valid YAML or does not have the profile's shape.</summary>
public sealed class ProfileFormatException(string message) : Exception(message);

/// <summary>
/// Writes setup profiles as YAML in a fixed layout: the schema reference first, then every id followed by a
/// comment with its friendly name, aligned within each list.
/// </summary>
public static partial class ProfileYamlWriter
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/faulkner-engineering/hyperharbor/main/schemas/profile.v1.schema.json";

    /// <summary>Comments start at least this far in, so short lists line up as well.</summary>
    private const int MinimumCommentColumn = 32;

    /// <summary>Lines longer than this put their comment two spaces after the value instead of aligning it.</summary>
    private const int MaximumCommentColumn = 64;

    [GeneratedRegex(@"^[A-Za-z0-9_\\][A-Za-z0-9 _.,()&+/\\~@'=%!*-]*$")]
    private static partial Regex PlainSafe();

    [GeneratedRegex(@"^[-+]?(\d[\d_]*(\.\d*)?|\.\d+)([eE][-+]?\d+)?$|^0[xo][0-9a-fA-F]+$")]
    private static partial Regex LooksNumeric();

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "null", "y", "n", "~",
    };

    /// <param name="policyTypes">The catalog type of each policy key (boolean, integer, string), to write values unquoted where they are typed.</param>
    public static string Write(SetupProfile profile, IReadOnlyDictionary<string, string>? policyTypes = null)
    {
        var lines = new List<string>
        {
            $"# yaml-language-server: $schema={SchemaUrl}",
            "schemaVersion: 1",
            $"name: {Scalar(profile.Name)}",
        };
        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            lines.Add($"description: {Scalar(profile.Description)}");
        }

        if (profile.Install is { Count: > 0 } install)
        {
            lines.Add("install:");
            AddItems(lines, "  ", install);
        }

        var remove = profile.Remove;
        if (remove is not null && (remove.Appx?.Count > 0 || remove.Capabilities?.Count > 0 || remove.Features?.Count > 0))
        {
            lines.Add("remove:");
            AddSection(lines, "appx", remove.Appx);
            AddSection(lines, "capabilities", remove.Capabilities);
            AddSection(lines, "features", remove.Features);
        }

        if (profile.Tweaks is { Count: > 0 } tweaks)
        {
            lines.Add("tweaks:");
            var block = new List<(string Text, string? Comment)>();
            foreach (var tweak in tweaks)
            {
                if (tweak.Registry is { } registry)
                {
                    Flush(lines, block);
                    lines.Add(WithComment("  - registry:", tweak.Name, MinimumCommentColumn));
                    lines.Add($"      key: {Scalar(registry.Key)}");
                    lines.Add($"      name: {Scalar(registry.Name)}");
                    lines.Add($"      type: {registry.Type.ToString().ToLowerInvariant()}");
                    lines.Add($"      value: {(registry.Type == RegistryValueType.String ? Scalar(registry.Value) : registry.Value)}");
                }
                else
                {
                    block.Add(($"  - {Scalar(tweak.Id ?? "")}", tweak.Name));
                }
            }

            Flush(lines, block);
        }

        if (profile.Browser is { } browser)
        {
            lines.Add("browser:");
            lines.Add(WithComment($"  app: {Scalar(browser.App.Id)}", browser.App.Name, MinimumCommentColumn));
            if (browser.Extensions is { Count: > 0 } extensions)
            {
                lines.Add("  extensions:");
                AddItems(lines, "    ", extensions);
            }

            if (browser.Policies is { Count: > 0 } policies)
            {
                lines.Add("  policies:");
                foreach (var (key, value) in policies.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    lines.Add($"    {key}: {PolicyValue(value, policyTypes?.GetValueOrDefault(key))}");
                }
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Plain when that reads back as the same string, otherwise single-quoted (or double-quoted for control characters).</summary>
    public static string Scalar(string value)
    {
        if (value.Length > 0 && value.Trim() == value && PlainSafe().IsMatch(value) && !Reserved.Contains(value) && !LooksNumeric().IsMatch(value))
        {
            return value;
        }

        if (value.All(character => !char.IsControl(character)))
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                _ => character.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    private static string PolicyValue(string value, string? type) => type switch
    {
        "boolean" when bool.TryParse(value, out var flag) => flag ? "true" : "false",
        "integer" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number.ToString(CultureInfo.InvariantCulture),
        _ => Scalar(value),
    };

    private static void AddSection(List<string> lines, string name, IReadOnlyList<ProfileItem>? items)
    {
        if (items is { Count: > 0 })
        {
            lines.Add($"  {name}:");
            AddItems(lines, "    ", items);
        }
    }

    private static void AddItems(List<string> lines, string indent, IReadOnlyList<ProfileItem> items)
    {
        var block = items.Select(item => ($"{indent}- {Scalar(item.Id)}", item.Name)).ToList();
        Flush(lines, block);
    }

    private static void Flush(List<string> lines, List<(string Text, string? Comment)> block)
    {
        if (block.Count == 0)
        {
            return;
        }

        var column = Math.Max(MinimumCommentColumn, block.Where(line => line.Text.Length + 2 <= MaximumCommentColumn).Select(line => line.Text.Length + 2).DefaultIfEmpty(0).Max());
        foreach (var (text, comment) in block)
        {
            lines.Add(WithComment(text, comment, column));
        }

        block.Clear();
    }

    private static string WithComment(string text, string? comment, int column)
    {
        var clean = comment is null ? "" : string.Join(' ', comment.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (clean.Length == 0)
        {
            return text;
        }

        var padding = text.Length + 2 <= column ? column - text.Length : 2;
        return text + new string(' ', padding) + "# " + clean;
    }
}

/// <summary>
/// Reads setup profile YAML into a <see cref="SetupProfile"/>. Trailing comments on id lines become the items'
/// names, so a profile written by <see cref="ProfileYamlWriter"/> reads back unchanged.
/// </summary>
public static class ProfileYamlReader
{
    public const int SchemaVersion = 1;

    private static readonly string[] TopLevel = ["schemaVersion", "name", "description", "install", "remove", "tweaks", "browser"];

    /// <exception cref="ProfileFormatException">Not YAML, or not a profile.</exception>
    public static SetupProfile Read(string yaml)
    {
        var comments = InlineComments(yaml);
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
            throw new ProfileFormatException("A profile is one YAML document with a mapping at the top.");
        }

        CheckKeys(root, TopLevel, "the profile");
        var version = OptionalScalar(root, "schemaVersion");
        if (version != SchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new ProfileFormatException($"schemaVersion must be {SchemaVersion}.");
        }

        var name = OptionalScalar(root, "name") ?? throw new ProfileFormatException("name is required.");
        return new SetupProfile(
            name,
            OptionalScalar(root, "description"),
            Items(root, "install", comments),
            Remove(root, comments),
            Tweaks(root, comments),
            Browser(root, comments));
    }

    private static ProfileRemove? Remove(YamlMappingNode root, IReadOnlyDictionary<long, string> comments)
    {
        if (Child(root, "remove") is not { } node)
        {
            return null;
        }

        var remove = node as YamlMappingNode ?? throw new ProfileFormatException($"Line {node.Start.Line}: remove must be a mapping.");
        CheckKeys(remove, ["appx", "capabilities", "features"], "remove");
        return new ProfileRemove(Items(remove, "appx", comments), Items(remove, "capabilities", comments), Items(remove, "features", comments));
    }

    private static IReadOnlyList<ProfileTweak>? Tweaks(YamlMappingNode root, IReadOnlyDictionary<long, string> comments)
    {
        if (Child(root, "tweaks") is not { } node)
        {
            return null;
        }

        var list = node as YamlSequenceNode ?? throw new ProfileFormatException($"Line {node.Start.Line}: tweaks must be a list.");
        var tweaks = new List<ProfileTweak>();
        foreach (var item in list)
        {
            switch (item)
            {
                case YamlScalarNode scalar:
                    tweaks.Add(new ProfileTweak(scalar.Value ?? "", comments.GetValueOrDefault(scalar.Start.Line)));
                    break;
                case YamlMappingNode mapping:
                    CheckKeys(mapping, ["registry"], "a tweak");
                    var registry = Child(mapping, "registry") as YamlMappingNode
                        ?? throw new ProfileFormatException($"Line {mapping.Start.Line}: a custom tweak is registry: with key, name, type, and value.");
                    CheckKeys(registry, ["key", "name", "type", "value"], "registry");
                    var type = OptionalScalar(registry, "type") switch
                    {
                        "dword" => RegistryValueType.Dword,
                        "qword" => RegistryValueType.Qword,
                        "string" => RegistryValueType.String,
                        var other => throw new ProfileFormatException($"Line {registry.Start.Line}: type must be dword, qword, or string, not \"{other}\"."),
                    };
                    var keyLine = mapping.Children.Keys.First().Start.Line;
                    tweaks.Add(new ProfileTweak(
                        Name: comments.GetValueOrDefault(keyLine),
                        Registry: new RegistryTweak(
                            OptionalScalar(registry, "key") ?? "",
                            OptionalScalar(registry, "name") ?? "",
                            type,
                            OptionalScalar(registry, "value") ?? "")));
                    break;
                default:
                    throw new ProfileFormatException($"Line {item.Start.Line}: a tweak is a tweak id or registry: with a value.");
            }
        }

        return tweaks;
    }

    private static ProfileBrowser? Browser(YamlMappingNode root, IReadOnlyDictionary<long, string> comments)
    {
        if (Child(root, "browser") is not { } node)
        {
            return null;
        }

        var browser = node as YamlMappingNode ?? throw new ProfileFormatException($"Line {node.Start.Line}: browser must be a mapping.");
        CheckKeys(browser, ["app", "extensions", "policies"], "browser");
        var app = Child(browser, "app") as YamlScalarNode ?? throw new ProfileFormatException($"Line {browser.Start.Line}: browser needs app, the browser's winget id or alias.");

        Dictionary<string, string>? policies = null;
        if (Child(browser, "policies") is { } policyNode)
        {
            var mapping = policyNode as YamlMappingNode ?? throw new ProfileFormatException($"Line {policyNode.Start.Line}: policies must be a mapping of policy keys to values.");
            policies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in mapping.Children)
            {
                if (key is not YamlScalarNode { Value: { } policyKey } || value is not YamlScalarNode { Value: { } policyValue })
                {
                    throw new ProfileFormatException($"Line {key.Start.Line}: each policy is key: value.");
                }

                policies[policyKey] = policyValue;
            }
        }

        return new ProfileBrowser(
            new ProfileItem(app.Value ?? "", comments.GetValueOrDefault(app.Start.Line)),
            Items(browser, "extensions", comments),
            policies);
    }

    private static List<ProfileItem>? Items(YamlMappingNode parent, string key, IReadOnlyDictionary<long, string> comments)
    {
        if (Child(parent, key) is not { } node)
        {
            return null;
        }

        if (node is YamlScalarNode { Value: null or "" })
        {
            return [];
        }

        var list = node as YamlSequenceNode ?? throw new ProfileFormatException($"Line {node.Start.Line}: {key} must be a list.");
        return list.Children
            .Select(item => item is YamlScalarNode { Value: { } value }
                ? new ProfileItem(value, comments.GetValueOrDefault(item.Start.Line))
                : throw new ProfileFormatException($"Line {item.Start.Line}: each {key} entry is a single id."))
            .ToList();
    }

    private static YamlNode? Child(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static string? OptionalScalar(YamlMappingNode mapping, string key) => Child(mapping, key) switch
    {
        null => null,
        YamlScalarNode scalar => scalar.Value,
        var other => throw new ProfileFormatException($"Line {other.Start.Line}: {key} must be a single value."),
    };

    private static void CheckKeys(YamlMappingNode mapping, string[] allowed, string where)
    {
        foreach (var key in mapping.Children.Keys)
        {
            if (key is not YamlScalarNode { Value: { } name } || !allowed.Contains(name, StringComparer.Ordinal))
            {
                throw new ProfileFormatException($"Line {key.Start.Line}: {where} does not have \"{key}\". Allowed: {string.Join(", ", allowed)}.");
            }
        }
    }

    /// <summary>Comments that follow a value on the same line, by line number.</summary>
    private static Dictionary<long, string> InlineComments(string yaml)
    {
        var comments = new Dictionary<long, string>();
        try
        {
            var scanner = new Scanner(new StringReader(yaml), skipComments: false);
            while (scanner.MoveNext())
            {
                if (scanner.Current is Comment { IsInline: true } comment)
                {
                    comments[comment.Start.Line] = comment.Value.Trim();
                }
            }
        }
        catch (YamlException)
        {
            // Loading the document reports the error with its position.
        }

        return comments;
    }
}
