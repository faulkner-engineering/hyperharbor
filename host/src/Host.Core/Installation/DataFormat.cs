using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>Thrown when the data directory was written by a newer version than this one.</summary>
public sealed class DataFormatTooNewException(int found, int supported)
    : Exception($"The data in this host's data folder is format {found}, written by a newer version of HyperHarbor. " +
        $"This version reads format {supported} and earlier. Install the newer version again, or restore a backup.")
{
    public int Found { get; } = found;

    public int Supported { get; } = supported;
}

/// <summary>
/// The format of the files in the data directory, recorded in data-format.json. A version starts only on data
/// it understands: older formats are migrated forward when it starts, and a newer format stops it, so an
/// older version (after a rollback or a manual downgrade) never misreads or overwrites newer data.
/// </summary>
public static class DataFormat
{
    public const string FileName = "data-format.json";

    /// <summary>Raise this, and add the step to <see cref="Migrations"/>, when a release changes how data is written.</summary>
    public const int Current = 1;

    /// <summary>Steps from format N to N + 1, keyed by N, run in order on the data directory.</summary>
    private static readonly IReadOnlyDictionary<int, Action<string>> Migrations = new Dictionary<int, Action<string>>();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>The recorded format, or null when the file is missing (data from before the marker is format 1).</summary>
    /// <exception cref="InvalidDataException">The file is not a valid marker.</exception>
    public static int? Read(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(path), JsonOptions) is { Format: > 0 } marker
                ? marker.Format
                : throw new InvalidDataException($"{path} does not hold a data format number.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid JSON.", ex);
        }
    }

    /// <summary>Migrates older data to <see cref="Current"/> and records it. Leaves the data alone when it is newer.</summary>
    /// <returns>The format found before migrating.</returns>
    /// <exception cref="DataFormatTooNewException">The data was written by a newer version.</exception>
    public static int EnsureCurrent(string dataDirectory) => EnsureCurrent(dataDirectory, Current, Migrations);

    /// <summary>Exposed for tests, which supply their own format and steps.</summary>
    internal static int EnsureCurrent(string dataDirectory, int current, IReadOnlyDictionary<int, Action<string>> migrations)
    {
        var found = Read(dataDirectory) ?? 1;
        if (found > current)
        {
            throw new DataFormatTooNewException(found, current);
        }

        for (var format = found; format < current; format++)
        {
            if (!migrations.TryGetValue(format, out var migrate))
            {
                throw new InvalidOperationException($"There is no migration from data format {format} to {format + 1}.");
            }

            migrate(dataDirectory);
            Write(dataDirectory, format + 1);
        }

        if (Read(dataDirectory) != current)
        {
            Write(dataDirectory, current);
        }

        return found;
    }

    private static void Write(string dataDirectory, int format) =>
        ProtectedFile.WriteAllBytes(Path.Combine(dataDirectory, FileName), JsonSerializer.SerializeToUtf8Bytes(new Marker(format), JsonOptions));

    private sealed record Marker(int Format);
}
