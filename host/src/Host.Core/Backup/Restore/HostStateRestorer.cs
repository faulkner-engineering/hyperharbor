using System.Text.Json;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup.Restore;

/// <param name="Restored">Relative names written into the data directory.</param>
/// <param name="RestartRequired">The running host holds the old files in memory; it must restart to use the restored ones.</param>
public sealed record HostStateRestoreResult(IReadOnlyList<string> Restored, IReadOnlyList<string> Skipped, bool RestartRequired, IReadOnlyList<string> Warnings);

/// <summary>
/// Puts a host-state snapshot back: pairings, certificate, settings, profiles. Only names the backup itself would write are
/// accepted, so a tampered snapshot cannot write elsewhere. The backup configuration is kept as it is, because its keys live in
/// this PC's vault and a restored copy would point at keys this PC does not have.
/// </summary>
public sealed class HostStateRestorer(
    string dataDirectory,
    IBackupRepositories repositories,
    ISnapshotRestorer restorer,
    ILogger<HostStateRestorer> logger)
{
    private static readonly string[] KeptAsIs = [BackupConfigStore.FileName];

    public async Task<HostStateRestoreResult> RestoreAsync(BackupTarget target, string snapshotId, Action<string, int> progress, CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(dataDirectory, "backup", $"restore-host-{Guid.NewGuid():N}");
        try
        {
            progress("Opening the backup repository", 5);
            OpenRepository repository;
            try
            {
                repository = await repositories.OpenAsync(target, RepositoryRole.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BackupException or Kopia.KopiaException)
            {
                throw new RestoreException(ex.Message, ex);
            }

            progress("Restoring the files", 20);
            Directory.CreateDirectory(scratch);
            await restorer.RestoreAsync(repository, snapshotId, scratch, cancellationToken).ConfigureAwait(false);

            var markerPath = Path.Combine(scratch, HostStateMarker.FileName);
            if (!File.Exists(markerPath))
            {
                throw new RestoreException("The snapshot is not a HyperHarbor host-state backup (host-state.json is missing).");
            }

            HostStateMarker marker;
            try
            {
                marker = JsonSerializer.Deserialize<HostStateMarker>(await File.ReadAllBytesAsync(markerPath, cancellationToken).ConfigureAwait(false), BackupJson.Options)
                    ?? throw new RestoreException("host-state.json is empty.");
            }
            catch (JsonException ex)
            {
                throw new RestoreException("host-state.json is not valid.", ex);
            }

            if (marker.DataFormat > DataFormat.Current)
            {
                throw new RestoreException(
                    $"This host-state backup uses data format {marker.DataFormat}; this host reads format {DataFormat.Current} and earlier. Update the host first.");
            }

            progress("Writing the host's state", 60);
            var restored = new List<string>();
            var skipped = new List<string>();
            foreach (var name in marker.Files)
            {
                if (!Accepted(name))
                {
                    skipped.Add(name);
                    logger.LogWarning("Host-state restore skipped the unexpected entry {Name}.", name);
                    continue;
                }

                var source = Path.Combine(scratch, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    skipped.Add(name);
                    continue;
                }

                var destination = Path.Combine(dataDirectory, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ProtectedFile.WriteAllBytes(destination, await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false));
                restored.Add(name);
            }

            var warnings = new List<string>
            {
                "Credentials sealed to the original PC (VM account passwords, console accounts) are not part of the backup. Provision your VMs again.",
            };
            if (restored.Count == 0)
            {
                warnings.Add("Nothing was restored.");
            }

            progress("Done", 99);
            return new HostStateRestoreResult(restored, skipped, RestartRequired: restored.Count > 0, warnings);
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the host-state restore folder.");
            }
        }
    }

    /// <summary>A name the backup writes: an allowlisted top-level file, or a file under profiles/ without any ".." part.</summary>
    internal static bool Accepted(string name)
    {
        if (name.Contains('\\', StringComparison.Ordinal) || Path.IsPathRooted(name) || name.Split('/').Any(part => part is ".." or "." or ""))
        {
            return false;
        }

        if (KeptAsIs.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return HostStateBackupService.Files.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith(HostStateBackupService.ProfilesFolder + "/", StringComparison.OrdinalIgnoreCase);
    }
}
