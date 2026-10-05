using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// update\health.json: written by the host once it listens, removed when it stops. The update helper waits for
/// a report from the expected version that started after the update began, then checks the port with TLS.
/// There is no health endpoint: every route but pairing requires a paired device.
/// </summary>
public sealed record HealthReport(string Version, int ProcessId, string Address, int Port, DateTimeOffset StartedAt)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, DataBackup.UpdateFolderName, "health.json");

    public void Write(string dataDirectory) => ProtectedFile.WriteAllBytes(PathIn(dataDirectory), JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));

    /// <summary>The current report, or null when there is none or it cannot be read (being replaced).</summary>
    public static HealthReport? Read(string dataDirectory)
    {
        try
        {
            var path = PathIn(dataDirectory);
            return File.Exists(path) ? JsonSerializer.Deserialize<HealthReport>(Utf8Json.ReadFile(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string dataDirectory) => File.Delete(PathIn(dataDirectory));
}

/// <summary>Starts and stops the host service. Tests supply a fake.</summary>
public interface IServiceControl
{
    /// <exception cref="InvalidOperationException">The service did not start.</exception>
    Task StartAsync(CancellationToken cancellationToken);

    /// <exception cref="InvalidOperationException">The service did not stop.</exception>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Whether a started version is healthy. Tests supply a fake.</summary>
public interface IHealthProbe
{
    /// <returns>True when <paramref name="expected"/> reported healthy after <paramref name="since"/> within the timeout.</returns>
    Task<bool> WaitHealthyAsync(SemanticVersion expected, DateTimeOffset since, TimeSpan timeout, CancellationToken cancellationToken);
}
