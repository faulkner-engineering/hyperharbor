using System.Text.Json;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Installation;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>What a host-state snapshot holds, written beside the files as host-state.json.</summary>
public sealed record HostStateMarker(
    int DataFormat,
    string HyperHarborVersion,
    string MachineName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Files)
{
    public const string FileName = "host-state.json";
}

/// <summary>
/// Backs up the host's own state (pairings, certificate, settings, profiles, provisioning records) to the host repository
/// of a target. Secrets sealed to this PC with DPAPI (VM credentials, console accounts, the backup vault itself) are never
/// included: they cannot be opened elsewhere, and the backup passphrases must not sit in the backup they protect.
/// </summary>
public sealed class HostStateBackupService(
    string dataDirectory,
    IBackupRepositories repositories,
    KopiaCli kopia,
    BackupHistoryStore history,
    TimeProvider time,
    ILogger<HostStateBackupService> logger,
    string hostVersion)
{
    /// <summary>Top-level files of the data directory that make up the host state.</summary>
    public static readonly IReadOnlyList<string> Files =
    [
        "data-format.json", "users.json", "paired-devices.json", "host-identity.json", "host-certificate.pfx.protected",
        "admin-passphrase.json", "host-settings.json", "vm-provisioning.json", "unattend-profiles.json", "unattended-installs.json",
        "appx-baselines.json", "performance.json", "ambient.json", "power-mode.json", "backup-config.json",
    ];

    public const string ProfilesFolder = "profiles";

    public string StagingFolder => Path.Combine(dataDirectory, "backup", "stage-host");

    public async Task<BackupRunRecord> RunAsync(BackupTarget target, BackupTrigger trigger, Action<string, int> progress, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var started = time.GetUtcNow();
        var warnings = new List<string>();
        string? snapshotId = null;
        long bytes = 0;
        long? uploaded = null;
        var outcome = BackupOutcome.Failed;
        string? reason = null;
        try
        {
            progress("Opening the backup repository", 5);
            var repository = await repositories.OpenAsync(target, RepositoryRole.Host, cancellationToken).ConfigureAwait(false);

            progress("Collecting the host's state", 15);
            var collected = Collect();
            try
            {
                progress("Setting retention", 30);
                await kopia.SetPolicyAsync(repository.Repository, repository.Passphrase, StagingFolder, target.DefaultRetention, cancellationToken).ConfigureAwait(false);

                progress("Uploading", 40);
                var (snapshot, measured) = await kopia.CreateMeasuredSnapshotAsync(
                    repository.Repository, repository.Passphrase, StagingFolder, $"hyperharbor host-state run={runId:N}", cancellationToken).ConfigureAwait(false);
                snapshotId = snapshot.Id;
                bytes = snapshot.TotalSize;
                uploaded = measured;
                outcome = BackupOutcome.Succeeded;
                if (collected.Count == 0)
                {
                    warnings.Add("The host had no state files to back up.");
                }
            }
            finally
            {
                RemoveStaging(warnings);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "Cancelled";
        }
        catch (Exception ex) when (ex is BackupException or KopiaException or KopiaUnavailableException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Host state backup to {Target} failed.", target.Id);
            reason = ex.Message;
        }

        var record = new BackupRunRecord(runId, "host", null, null, target.Id, trigger, started, time.GetUtcNow(), outcome, reason, snapshotId, bytes, warnings, uploaded);
        history.Add(record);
        progress("Done", 99);
        return record;
    }

    /// <summary>Copies the allowlisted files into a fresh staging folder and writes the marker. Returns the relative names.</summary>
    internal IReadOnlyList<string> Collect()
    {
        RemoveStaging([]);
        Directory.CreateDirectory(StagingFolder);
        var copied = new List<string>();
        foreach (var name in Files)
        {
            var source = Path.Combine(dataDirectory, name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(StagingFolder, name));
                copied.Add(name);
            }
        }

        var profiles = Path.Combine(dataDirectory, ProfilesFolder);
        if (Directory.Exists(profiles))
        {
            foreach (var file in Directory.EnumerateFiles(profiles, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dataDirectory, file);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(StagingFolder, relative))!);
                File.Copy(file, Path.Combine(StagingFolder, relative));
                copied.Add(relative.Replace('\\', '/'));
            }
        }

        var marker = new HostStateMarker(DataFormat.Current, hostVersion, Environment.MachineName, time.GetUtcNow(), copied);
        File.WriteAllBytes(Path.Combine(StagingFolder, HostStateMarker.FileName), JsonSerializer.SerializeToUtf8Bytes(marker, BackupJson.Options));
        return copied;
    }

    private void RemoveStaging(List<string> warnings)
    {
        try
        {
            if (Directory.Exists(StagingFolder))
            {
                Directory.Delete(StagingFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The host state staging folder could not be removed.");
            warnings.Add("The temporary host state folder could not be removed; the next backup removes it.");
        }
    }
}
