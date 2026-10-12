namespace HyperHarbor.Host.Core.Backup.Kopia;

/// <summary>
/// Typed kopia commands. The repository passphrase goes through KOPIA_PASSWORD, and kopia's configuration, cache, and
/// log folders through environment variables, so nothing sensitive is on a command line except the storage keys that
/// kopia accepts only as flags (they are visible only to administrators, since the host runs as SYSTEM).
/// </summary>
public sealed class KopiaCli(IKopiaRunner runner)
{
    public static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(12);

    public static IReadOnlyDictionary<string, string> Environment(KopiaRepository repository, string passphrase) =>
        new Dictionary<string, string>
        {
            ["KOPIA_PASSWORD"] = passphrase,
            ["KOPIA_CONFIG_PATH"] = repository.ConfigFile,
            ["KOPIA_CACHE_DIRECTORY"] = repository.CacheFolder,
            ["KOPIA_LOG_DIR"] = repository.LogFolder,
        };

    /// <summary>Arguments of <c>repository create</c> or <c>repository connect</c>.</summary>
    public static IReadOnlyList<string> RepositoryArguments(string verb, KopiaRepository repository, bool readOnly = false)
    {
        var arguments = new List<string> { "repository", verb };
        arguments.AddRange(repository.Backend.Arguments);
        arguments.AddRange(repository.Backend.SecretArguments);
        if (verb == "create")
        {
            arguments.AddRange(["--block-hash=BLAKE2B-256-128", "--encryption=AES256-GCM-HMAC-SHA256"]);
        }
        else if (readOnly)
        {
            arguments.Add("--readonly");
        }

        return arguments;
    }

    /// <summary>The kopia policy arguments for retention: only the daily, weekly, and monthly counts are kept.</summary>
    public static IReadOnlyList<string> PolicyArguments(string sourcePath, RetentionPolicy retention) =>
    [
        "policy", "set", sourcePath,
        "--keep-latest=0", "--keep-hourly=0", $"--keep-daily={retention.Daily}",
        $"--keep-weekly={retention.Weekly}", $"--keep-monthly={retention.Monthly}", "--keep-annual=0",
        "--compression=zstd",
    ];

    /// <summary>Kilobits per second to kopia's bytes per second; no limit is 0.</summary>
    public static IReadOnlyList<string> ThrottleArguments(int? kilobitsPerSecond) =>
    [
        "repository", "throttle", "set",
        $"--upload-bytes-per-second={(kilobitsPerSecond is { } limit ? limit * 125L : 0L)}",
    ];

    public Task CreateRepositoryAsync(KopiaRepository repository, string passphrase, CancellationToken ct) =>
        RunAsync(repository, passphrase, RepositoryArguments("create", repository), ShortTimeout, ct);

    public Task ConnectAsync(KopiaRepository repository, string passphrase, bool readOnly, CancellationToken ct) =>
        RunAsync(repository, passphrase, RepositoryArguments("connect", repository, readOnly), ShortTimeout, ct);

    public Task SetPolicyAsync(KopiaRepository repository, string passphrase, string sourcePath, RetentionPolicy retention, CancellationToken ct) =>
        RunAsync(repository, passphrase, PolicyArguments(sourcePath, retention), ShortTimeout, ct);

    public Task SetBandwidthAsync(KopiaRepository repository, string passphrase, int? kilobitsPerSecond, CancellationToken ct) =>
        RunAsync(repository, passphrase, ThrottleArguments(kilobitsPerSecond), ShortTimeout, ct);

    public async Task<KopiaSnapshot> CreateSnapshotAsync(
        KopiaRepository repository, string passphrase, string sourcePath, string description, CancellationToken ct)
    {
        var result = await RunAsync(
            repository, passphrase, ["snapshot", "create", sourcePath, "--json", $"--description={description}"], TransferTimeout, ct)
            .ConfigureAwait(false);
        return KopiaOutput.CreatedSnapshot(result.StandardOutput);
    }

    public async Task<IReadOnlyList<KopiaSnapshot>> ListSnapshotsAsync(KopiaRepository repository, string passphrase, CancellationToken ct) =>
        KopiaOutput.Snapshots(
            (await RunAsync(repository, passphrase, ["snapshot", "list", "--all", "--json"], ShortTimeout, ct).ConfigureAwait(false)).StandardOutput);

    public Task RestoreAsync(KopiaRepository repository, string passphrase, string snapshotId, string destination, CancellationToken ct) =>
        RunAsync(repository, passphrase, ["snapshot", "restore", snapshotId, destination, "--write-sparse-files"], TransferTimeout, ct);

    public Task DeleteSnapshotAsync(KopiaRepository repository, string passphrase, string snapshotId, CancellationToken ct) =>
        RunAsync(repository, passphrase, ["snapshot", "delete", snapshotId, "--delete"], ShortTimeout, ct);

    /// <summary>Runs a command in a repository's context; throws <see cref="KopiaException"/> on failure.</summary>
    public async Task<KopiaResult> RunAsync(
        KopiaRepository repository, string passphrase, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var result = await runner.RunAsync(new KopiaInvocation(arguments, Environment(repository, passphrase), timeout), ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return result;
        }

        var secrets = repository.Backend.SecretArguments.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Append(passphrase);
        var detail = KopiaOutput.Sanitize(result.StandardError, secrets);
        throw new KopiaException($"kopia failed (exit code {result.ExitCode}): {detail}", KopiaOutput.Classify(result.StandardError));
    }
}
