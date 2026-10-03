using System.Collections;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>
/// Hyper-V methods take and return settings objects as "embedded instances": strings in the DMTF
/// CIM-XML (DTD 2.0) INSTANCE format, the same format WMI's GetText(1) produces. This class writes
/// them from a <see cref="CimInstance"/> or a property list, and reads them back.
/// </summary>
internal static class CimXml
{
    /// <summary>A property to write. Arrays are given as an <see cref="IEnumerable"/> of element values.</summary>
    public sealed record Property(string Name, CimType Type, object? Value);

    /// <summary>
    /// Writes every property of <paramref name="instance"/>, with <paramref name="changes"/> replacing or
    /// adding values. Reference and embedded object properties are left out; Hyper-V settings classes
    /// keep their links (such as Parent) in string properties.
    /// </summary>
    public static string Write(CimInstance instance, params Property[] changes)
    {
        var properties = new List<Property>();
        foreach (var property in instance.CimInstanceProperties)
        {
            if (changes.Any(change => string.Equals(change.Name, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            properties.Add(new Property(property.Name, property.CimType, property.Value));
        }

        properties.AddRange(changes);
        return Write(instance.CimSystemProperties.ClassName, properties);
    }

    /// <summary>Writes a new instance with only the given properties; Hyper-V uses defaults for the rest.</summary>
    public static string Write(string className, IEnumerable<Property> properties)
    {
        var element = new XElement("INSTANCE", new XAttribute("CLASSNAME", className));
        foreach (var property in properties)
        {
            if (TypeName(property.Type) is not { } typeName)
            {
                continue;
            }

            if (IsArray(property.Type))
            {
                var array = new XElement("PROPERTY.ARRAY", new XAttribute("NAME", property.Name), new XAttribute("TYPE", typeName));
                if (property.Value is IEnumerable values and not string)
                {
                    array.Add(new XElement("VALUE.ARRAY", values.Cast<object?>().Select(value => new XElement("VALUE", Format(value)))));
                }

                element.Add(array);
            }
            else
            {
                var scalar = new XElement("PROPERTY", new XAttribute("NAME", property.Name), new XAttribute("TYPE", typeName));
                if (property.Value is not null)
                {
                    scalar.Add(new XElement("VALUE", Format(property.Value)));
                }

                element.Add(scalar);
            }
        }

        return element.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Reads the scalar properties of an embedded instance. Missing and null values are left out.</summary>
    public static Dictionary<string, string> ReadScalars(string xml)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = XElement.Parse(xml);
        foreach (var property in root.Descendants("PROPERTY"))
        {
            if (property.Attribute("NAME")?.Value is { } name && property.Element("VALUE")?.Value is { } value)
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static bool IsArray(CimType type) => type.ToString().EndsWith("Array", StringComparison.Ordinal);

    /// <summary>The CIM-XML TYPE attribute, or null for types that are not written.</summary>
    private static string? TypeName(CimType type)
    {
        var scalar = IsArray(type) ? type.ToString()[..^"Array".Length] : type.ToString();
        return scalar switch
        {
            "Boolean" => "boolean",
            "UInt8" => "uint8",
            "SInt8" => "sint8",
            "UInt16" => "uint16",
            "SInt16" => "sint16",
            "UInt32" => "uint32",
            "SInt32" => "sint32",
            "UInt64" => "uint64",
            "SInt64" => "sint64",
            "Real32" => "real32",
            "Real64" => "real64",
            "Char16" => "char16",
            "String" => "string",
            "DateTime" => "datetime",
            _ => null,
        };
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        DateTime time => FormatDateTime(time),
        DateTimeOffset time => FormatDateTime(time.UtcDateTime),
        TimeSpan interval => FormatInterval(interval),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>CIM datetime: yyyymmddHHMMSS.mmmmmm+UUU, in UTC.</summary>
    private static string FormatDateTime(DateTime time) =>
        time.ToUniversalTime().ToString("yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture) + "+000";

    /// <summary>CIM interval: ddddddddHHMMSS.mmmmmm:000.</summary>
    private static string FormatInterval(TimeSpan interval) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{interval.Days:D8}{interval.Hours:D2}{interval.Minutes:D2}{interval.Seconds:D2}.{interval.Ticks % TimeSpan.TicksPerSecond / 10:D6}:000");
}
