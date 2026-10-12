using System.Security.Cryptography;
using System.Text.Json;

namespace HyperHarbor.Host.Core.Backup.Kopia;

/// <summary>The pinned tool facts from tools/tools.json.</summary>
public sealed record KopiaToolInfo(string Version, string ExeSha256);

/// <summary>Where the bundled kopia.exe comes from: the host executable's embedded resources, or nothing in development.</summary>
public interface IToolPayload
{
    /// <summary>The pinned version and hash, or null when this build carries no tools manifest.</summary>
    KopiaToolInfo? Kopia { get; }

    /// <summary>The embedded kopia.exe, or null when this build has none (development builds).</summary>
    Stream? OpenKopia();
}

public sealed class KopiaUnavailableException(string message) : Exception(message);

/// <summary>
/// Finds the Kopia to run. The bundled copy is extracted to <c>tools\kopia.exe</c> beside the version's executable
/// (under Program Files for an installed host, which only administrators can write) and its SHA-256 is checked
/// against the pinned hash before every use, so a replaced or damaged file is never run. A build without the payload
/// uses the file named by HH_KOPIA_EXE, whose hash is not pinned (development only).
/// </summary>
public sealed class KopiaToolLocator(IToolPayload payload, string toolsFolder, Func<string, string?>? environment = null)
{
    public const string FileName = "kopia.exe";

    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;
    private readonly object _gate = new();

    public static KopiaToolInfo? ParseManifest(Stream json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var kopia = document.RootElement.GetProperty("kopia");
            return new KopiaToolInfo(kopia.GetProperty("version").GetString()!, kopia.GetProperty("exeSha256").GetString()!.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The verified path of kopia.exe, extracting it first when needed.</summary>
    /// <exception cref="KopiaUnavailableException">This build has no Kopia, or the file on disk does not match the pinned hash.</exception>
    public string Ensure()
    {
        lock (_gate)
        {
            var target = Path.Combine(toolsFolder, FileName);
            if (payload.Kopia is not { } info || payload.OpenKopia() is not { } source)
            {
                if (_environment("HH_KOPIA_EXE") is { Length: > 0 } development && File.Exists(development))
                {
                    return development;
                }

                throw new KopiaUnavailableException(
                    "This build of HyperHarbor does not include Kopia, so backups are not available. Install a release build.");
            }

            using (source)
            {
                if (File.Exists(target) && HashOf(target) == info.ExeSha256)
                {
                    return target;
                }

                Extract(source, target);
            }

            return HashOf(target) == info.ExeSha256
                ? target
                : throw new KopiaUnavailableException("The Kopia executable does not match the version this release pins. Reinstall HyperHarbor.");
        }
    }

    private static void Extract(Stream source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new KopiaUnavailableException($"Kopia could not be written to {target}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new KopiaUnavailableException($"Kopia could not be written to {target}: {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
