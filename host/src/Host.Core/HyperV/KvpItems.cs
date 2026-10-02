using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>
/// Parses Hyper-V data exchange (KVP) items. Each item is an embedded Msvm_KvpExchangeDataItem
/// instance serialized as CIM-XML, with Name and Data properties.
/// </summary>
public static class KvpItems
{
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            XElement instance;
            try
            {
                instance = XElement.Parse(item);
            }
            catch (XmlException)
            {
                continue;
            }

            string? Property(string name) => instance.Elements("PROPERTY")
                .FirstOrDefault(property => string.Equals((string?)property.Attribute("NAME"), name, StringComparison.OrdinalIgnoreCase))
                ?.Element("VALUE")?.Value;

            if (Property("Name") is { Length: > 0 } key && Property("Data") is { } data)
            {
                values[key] = data;
            }
        }

        return values;
    }

    public static GuestOsRow ToGuestOsRow(Guid vmId, IEnumerable<string> items)
    {
        var values = Parse(items);
        string? Value(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        return new GuestOsRow(
            vmId,
            Value("OSName"),
            Value("OSMajorVersion"),
            int.TryParse(Value("OSPlatformId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var platform) ? platform : null);
    }
}
