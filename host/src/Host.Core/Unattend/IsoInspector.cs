using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>
/// Reads what an ISO installs without mounting it: Windows Setup media (sources\install.wim or
/// install.esd, whose XML lists the editions) or an Ubuntu installer (casper\ and .disk\info).
/// Microsoft's Windows ISOs are UDF only (their ISO 9660 part holds just a README), so UDF is tried
/// first. Results are cached by path, size, and modification time.
/// </summary>
public sealed class IsoInspector
{
    private static readonly IsoInspection Unknown = new(null, null, []);
    private static readonly string[] WimFiles = [@"sources\install.wim", @"sources\install.esd"];

    private readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), IsoInspection> _cache = new();

    /// <exception cref="IOException">The image could not be read.</exception>
    public IsoInspection Inspect(string isoPath)
    {
        var file = new FileInfo(isoPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"The image {isoPath} does not exist.", isoPath);
        }

        return _cache.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), key => Read(key.Path));
    }

    private static IsoInspection Read(string isoPath)
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var fileSystem = Open(stream);
        if (fileSystem is null)
        {
            return Unknown;
        }

        foreach (var wim in WimFiles)
        {
            if (fileSystem.FileExists(wim))
            {
                using var wimStream = fileSystem.OpenFile(wim, FileMode.Open, FileAccess.Read);
                return new IsoInspection(InstallOs.Windows, "Windows", WimEditions(wimStream));
            }
        }

        // Joliet names without an extension can come back with a trailing period.
        if (fileSystem.DirectoryExists("casper") && new[] { @".disk\info", @".disk\info." }.FirstOrDefault(fileSystem.FileExists) is { } infoPath)
        {
            var info = Encoding.UTF8.GetString(fileSystem.ReadAllBytes(infoPath)).Split('\n')[0].Trim();
            return info.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase)
                ? new IsoInspection(InstallOs.Linux, Shorten(info), [])
                : Unknown;
        }

        return Unknown;
    }

    private static DiscFileSystem? Open(Stream stream)
    {
        if (UdfReader.Detect(stream))
        {
            stream.Position = 0;
            return new UdfReader(stream);
        }

        stream.Position = 0;
        if (CDReader.Detect(stream))
        {
            stream.Position = 0;
            return new CDReader(stream, joliet: true);
        }

        return null;
    }

    /// <summary>
    /// The image names in a WIM (or ESD). The header's XML resource is stored uncompressed as UTF-16;
    /// its location is the resource header at offset 72: a 56-bit size and flags, then the offset.
    /// </summary>
    internal static IReadOnlyList<string> WimEditions(Stream wim)
    {
        var header = new byte[208];
        wim.Position = 0;
        wim.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual("MSWIM\0\0\0"u8))
        {
            return [];
        }

        var xmlSize = (long)(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72)) & 0x00FF_FFFF_FFFF_FFFF);
        var xmlOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(80));
        if (xmlSize is <= 2 or > 16 * 1024 * 1024 || xmlOffset <= 0 || xmlOffset + xmlSize > wim.Length)
        {
            return [];
        }

        var xmlBytes = new byte[xmlSize];
        wim.Position = xmlOffset;
        wim.ReadExactly(xmlBytes);
        var xml = Encoding.Unicode.GetString(xmlBytes).TrimStart('﻿');
        return XDocument.Parse(xml).Root?
            .Elements("IMAGE")
            .OrderBy(image => int.TryParse((string?)image.Attribute("INDEX"), out var index) ? index : int.MaxValue)
            .Select(image => ((string?)image.Element("NAME"))?.Trim())
            .OfType<string>()
            .Where(name => name.Length > 0)
            .ToList() ?? [];
    }

    private static string Shorten(string value) => value.Length <= 100 ? value : value[..100];
}
