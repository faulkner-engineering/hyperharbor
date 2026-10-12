using HyperHarbor.Host.Core.Backup.Kopia;

namespace HyperHarbor.Host.Core.Backup;

public sealed record TargetTestStep(string Name, bool Passed, string Detail);

public sealed record TargetTestResult(IReadOnlyList<TargetTestStep> Steps)
{
    public bool Passed => Steps.All(step => step.Passed);
}

/// <summary>
/// The connection test for a backup target. A local folder is written to and read back. A remote target is checked
/// by connecting kopia to it: reaching it with no repository yet ("not initialized") proves the endpoint and the
/// keys; a wrong key or an unreachable host is reported in plain words.
/// </summary>
public sealed class BackupTargetTester(KopiaCli kopia, IBackupSecretStore secrets, string dataDirectory)
{
    public const long MinimumFreeBytes = 1L << 30;

    public async Task<TargetTestResult> TestAsync(BackupTarget target, CancellationToken ct)
    {
        var problems = target.Validate();
        if (problems.Count > 0)
        {
            return new TargetTestResult([new TargetTestStep("Settings", false, string.Join(' ', problems))]);
        }

        return target.Kind == BackupTargetKind.Local ? TestLocal(target.Local!.Path) : await TestRemoteAsync(target, ct).ConfigureAwait(false);
    }

    private static TargetTestResult TestLocal(string path)
    {
        var steps = new List<TargetTestStep>();
        try
        {
            Directory.CreateDirectory(path);
            steps.Add(new TargetTestStep("Folder", true, "The folder exists or was created."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            steps.Add(new TargetTestStep("Folder", false, $"The folder cannot be created: {ex.Message}"));
            return new TargetTestResult(steps);
        }

        var probe = Path.Combine(path, $".hyperharbor-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "probe");
            var readBack = File.ReadAllText(probe) == "probe";
            steps.Add(new TargetTestStep("Write", readBack, readBack ? "A test file was written and read back." : "The test file read back differently."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            steps.Add(new TargetTestStep("Write", false, $"The folder is not writable: {ex.Message}"));
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            steps.Add(new TargetTestStep("Free space", free >= MinimumFreeBytes, $"{free / (1024 * 1024 * 1024.0):0.#} GB free."));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Network shares have no drive information; the write test above is the real check.
            steps.Add(new TargetTestStep("Free space", true, "Free space could not be read (a network location); the write test passed."));
        }

        return new TargetTestResult(steps);
    }

    private async Task<TargetTestResult> TestRemoteAsync(BackupTarget target, CancellationToken ct)
    {
        KopiaRepository repository;
        try
        {
            repository = KopiaBackendFactory.For(target, RepositoryRole.Vm, $"test-{Guid.NewGuid():N}", dataDirectory, secrets);
        }
        catch (InvalidOperationException ex)
        {
            return new TargetTestResult([new TargetTestStep("Keys", false, ex.Message)]);
        }

        try
        {
            await kopia.ConnectAsync(repository, "connection-test-only", readOnly: true, ct).ConfigureAwait(false);
            return new TargetTestResult([new TargetTestStep("Connection", true, "Connected to an existing repository.")]);
        }
        catch (KopiaException ex) when (ex.Failure is KopiaFailure.NotARepository or KopiaFailure.BadPassphrase)
        {
            return new TargetTestResult([new TargetTestStep("Connection", true, "The storage is reachable and the keys are accepted.")]);
        }
        catch (KopiaException ex)
        {
            var detail = ex.Failure switch
            {
                KopiaFailure.AccessDenied => "The storage refused the keys. Check the access key, secret key, and permissions.",
                KopiaFailure.Unreachable => "The storage could not be reached. Check the endpoint and your network.",
                KopiaFailure.TimedOut => "The storage did not answer in time.",
                _ => ex.Message,
            };
            return new TargetTestResult([new TargetTestStep("Connection", false, detail)]);
        }
        finally
        {
            try
            {
                Directory.Delete(repository.StateFolder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
