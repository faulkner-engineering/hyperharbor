namespace HyperHarbor.Host.Core.Backup.Kopia;

/// <summary>Where one repository lives: the kopia storage subcommand with its flags.</summary>
public sealed record KopiaBackend(string Kind, IReadOnlyList<string> Arguments, IReadOnlyList<string> SecretArguments)
{
    /// <summary>Hides the keys that <see cref="SecretArguments"/> carries.</summary>
    public override string ToString() => $"KopiaBackend {{ {Kind} }}";
}

/// <summary>One repository: its backend and the folder holding kopia's configuration and cache for it.</summary>
public sealed record KopiaRepository(string RepoId, RepositoryRole Role, KopiaBackend Backend, string StateFolder)
{
    public string ConfigFile => Path.Combine(StateFolder, "repository.config");

    public string CacheFolder => Path.Combine(StateFolder, "cache");

    public string LogFolder => Path.Combine(StateFolder, "logs");
}

/// <summary>Maps a <see cref="BackupTarget"/> and role to the kopia backend and state folder.</summary>
public static class KopiaBackendFactory
{
    public static string RoleFolder(RepositoryRole role) => role == RepositoryRole.Vm ? "vm" : "host";

    public static KopiaRepository For(BackupTarget target, RepositoryRole role, string repoId, string dataDirectory, IBackupSecretStore secrets)
    {
        var folder = RoleFolder(role);
        var state = Path.Combine(dataDirectory, "backup", repoId);
        switch (target.Kind)
        {
            case BackupTargetKind.Local:
                return new KopiaRepository(repoId, role,
                    new KopiaBackend("filesystem", ["filesystem", "--path", Path.Combine(target.Local!.Path, folder)], []), state);

            case BackupTargetKind.S3:
            {
                var s3 = target.S3!;
                var access = secrets.Get(s3.AccessKeyRef) ?? throw new InvalidOperationException("The S3 access key is missing.");
                var secret = secrets.Get(s3.SecretKeyRef) ?? throw new InvalidOperationException("The S3 secret key is missing.");
                var arguments = new List<string> { "s3", "--bucket", s3.Bucket, "--endpoint", s3.Endpoint, "--prefix", Prefix(s3.Prefix, folder) };
                if (!string.IsNullOrWhiteSpace(s3.Region))
                {
                    arguments.AddRange(["--region", s3.Region]);
                }

                if (!s3.UseTls)
                {
                    arguments.Add("--disable-tls");
                }

                return new KopiaRepository(repoId, role,
                    new KopiaBackend("s3", arguments, ["--access-key", access, "--secret-access-key", secret]), state);
            }

            case BackupTargetKind.Azure:
            {
                var azure = target.Azure!;
                var key = secrets.Get(azure.KeyRef) ?? throw new InvalidOperationException("The Azure storage key is missing.");
                return new KopiaRepository(repoId, role,
                    new KopiaBackend("azure",
                        ["azure", "--container", azure.Container, "--storage-account", azure.Account, "--prefix", Prefix(azure.Prefix, folder)],
                        ["--storage-key", key]),
                    state);
            }

            default:
                throw new NotSupportedException($"Backup target kind {target.Kind} is not supported.");
        }
    }

    private static string Prefix(string? prefix, string folder) =>
        string.IsNullOrWhiteSpace(prefix) ? $"{folder}/" : $"{prefix.Trim('/')}/{folder}/";
}
