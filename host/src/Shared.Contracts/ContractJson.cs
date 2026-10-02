using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts;

/// <summary>
/// JSON settings that match the wire format defined in docs/api.yaml.
/// </summary>
public static class ContractJson
{
    /// <summary>
    /// camelCase property names and camelCase string enumerations.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Applies the contract settings to an existing options instance, such as the one owned by ASP.NET Core.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
