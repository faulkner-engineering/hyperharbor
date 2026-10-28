namespace HyperHarbor.Host.Core.Backup.Kopia;

/// <summary>
/// Typed kopia commands. The repository passphrase goes through KOPIA_PASSWORD, and kopia's configuration, cache, and
/// log folders through environment variables, so nothing sensitive is on a command line: the storage keys go
/// through the AWS_* and AZURE_STORAGE_KEY variables kopia reads.
/// </summary>
public sealed class KopiaCli(IKopiaRunner runner)
{
    public static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(12);

    public static IReadOnlyDictionary<string, string> Environment(KopiaRepository repository, string passphrase) =>
        new Dictionary<string, string>(repository.Backend.SecretEnvironment)
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

    /// <summary>
    /// The splitter for virtual disks. A disk image never shifts its data, so fixed 1 MiB pieces dedup as well as
    /// content-defined ones, and a small write costs about 1 MiB instead of kopia's default of about 4 MiB. That matters
    /// most for encrypted guests (BitLocker), whose changed pieces do not compress.
    /// </summary>
    public const string DiskSplitter = "FIXED-1M";

    /// <summary>
    /// The kopia policy arguments for retention (only the daily, weekly, and monthly counts are kept) and, when given,
    /// the splitter; without one the source keeps kopia's default.
    /// </summary>
    public static IReadOnlyList<string> PolicyArguments(string sourcePath, RetentionPolicy retention, string? splitter = null)
    {
        var arguments = new List<string>
        {
            "policy", "set", sourcePath,
            "--keep-latest=0", "--keep-hourly=0", $"--keep-daily={retention.Daily}",
            $"--keep-weekly={retention.Weekly}", $"--keep-monthly={retention.Monthly}", "--keep-annual=0",
            "--compression=zstd",
        };
        if (splitter is not null)
        {
            arguments.Add($"--splitter={splitter}");
        }

        return arguments;
    }

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

    public Task SetPolicyAsync(
        KopiaRepository repository, string passphrase, string sourcePath, RetentionPolicy retention, CancellationToken ct, string? splitter = null) =>
        RunAsync(repository, passphrase, PolicyArguments(sourcePath, retention, splitter), ShortTimeout, ct);

    /// <summary>The splitter a source's effective policy names, or null for kopia's default or when it cannot be read.</summary>
    public async Task<string?> ReadSplitterAsync(KopiaRepository repository, string passphrase, string sourcePath, CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(repository, passphrase, ["policy", "show", sourcePath, "--json"], ShortTimeout, ct).ConfigureAwait(false);
            return KopiaOutput.PolicySplitter(result.StandardOutput);
        }
        catch (KopiaException)
        {
            return null;
        }
    }

    /// <summary>
    /// The bytes the repository stores after dedup and compression, from <c>content stats</c> (which reads kopia's local
    /// index cache), or null when it cannot be read.
    /// </summary>
    public async Task<long?> StoredBytesAsync(KopiaRepository repository, string passphrase, CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(repository, passphrase, ["content", "stats", "--raw"], ShortTimeout, ct).ConfigureAwait(false);
            return KopiaOutput.StoredBytes(result.StandardOutput);
        }
        catch (KopiaException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a snapshot and measures what it added to the repository. The measurement is null when it could not be
    /// read, or when another snapshot ran in the same repository at the same time and some of the growth may be its.
    /// </summary>
    public async Task<(KopiaSnapshot Snapshot, long? UploadedBytes)> CreateMeasuredSnapshotAsync(
        KopiaRepository repository, string passphrase, string sourcePath, string description, CancellationToken ct)
    {
        var upload = UploadTracker.Begin(repository.ConfigFile);
        try
        {
            var before = await StoredBytesAsync(repository, passphrase, ct).ConfigureAwait(false);
            var snapshot = await CreateSnapshotAsync(repository, passphrase, sourcePath, description, ct).ConfigureAwait(false);
            var after = await StoredBytesAsync(repository, passphrase, ct).ConfigureAwait(false);
            return (snapshot, before is { } b && after is { } a && !upload.Overlapped ? Math.Max(0, a - b) : null);
        }
        finally
        {
            UploadTracker.End(repository.ConfigFile, upload);
        }
    }

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

        var secrets = repository.Backend.SecretEnvironment.Values.Append(passphrase);
        var detail = KopiaOutput.Sanitize(result.StandardError, secrets);
        throw new KopiaException($"kopia failed (exit code {result.ExitCode}): {detail}", KopiaOutput.Classify(result.StandardError));
    }

    /// <summary>
    /// The measured snapshots in progress per repository. Backups of different VMs can share a repository at the same
    /// time, and its growth then cannot be split between them, so each one that overlapped another is marked.
    /// </summary>
    private static class UploadTracker
    {
        private static readonly Dictionary<string, List<Upload>> Active = new(StringComparer.OrdinalIgnoreCase);

        public static Upload Begin(string repositoryKey)
        {
            var upload = new Upload();
            lock (Active)
            {
                if (!Active.TryGetValue(repositoryKey, out var uploads))
                {
                    Active[repositoryKey] = uploads = [];
                }

                if (uploads.Count > 0)
                {
                    upload.Overlapped = true;
                    uploads.ForEach(u => u.Overlapped = true);
                }

                uploads.Add(upload);
            }

            return upload;
        }

        public static void End(string repositoryKey, Upload upload)
        {
            lock (Active)
            {
                if (Active.TryGetValue(repositoryKey, out var uploads) && uploads.Remove(upload) && uploads.Count == 0)
                {
                    Active.Remove(repositoryKey);
                }
            }
        }
    }

    private sealed class Upload
    {
        private volatile bool _overlapped;

        public bool Overlapped
        {
            get => _overlapped;
            set => _overlapped = value;
        }
    }
}
