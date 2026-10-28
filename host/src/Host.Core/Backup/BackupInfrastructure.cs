using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.Backup;

public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The problem details code, when the client handles this failure specially.</summary>
    public string? Code { get; init; }
}

public sealed class BackupTargetNotFoundException() : Exception("The backup target does not exist.");

/// <summary>
/// The staging folder a backup snapshots: hard links to the VM's (frozen) disk files plus manifest.json, so Kopia reads
/// the real disks without a copy and sees one stable source per VM.
/// </summary>
public interface IBackupStaging
{
    /// <summary>Creates <paramref name="folder"/> empty, removing leftovers of an earlier run.</summary>
    void Prepare(string folder);

    /// <exception cref="BackupException">The link could not be made (a different volume, or a file system without hard links).</exception>
    void Link(string existingFile, string linkPath);

    long Length(string path);

    void WriteFile(string path, byte[] contents);

    void Remove(string folder);
}

public sealed class WindowsBackupStaging : IBackupStaging
{
    public void Prepare(string folder)
    {
        Remove(folder);
        var directory = Directory.CreateDirectory(folder);
        directory.Attributes |= FileAttributes.Hidden;
    }

    public void Link(string existingFile, string linkPath)
    {
        if (!CreateHardLinkW(linkPath, existingFile, IntPtr.Zero))
        {
            throw new BackupException(
                $"The disk {existingFile} could not be linked for backup: {new Win32Exception(Marshal.GetLastWin32Error()).Message} "
                + "All of a VM's disks must be on volumes that support hard links, in the same volume as the staging folder.");
        }
    }

    public long Length(string path) => new FileInfo(path).Length;

    public void WriteFile(string path, byte[] contents) => File.WriteAllBytes(path, contents);

    public void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}

public enum BackupTrigger
{
    Schedule,
    Shutdown,
    Manual,
}

public enum BackupOutcome
{
    Succeeded,
    Failed,
    Skipped,

    /// <summary>The backup is stored, but cleanup left something behind (for example a checkpoint that could not be removed).</summary>
    Partial,
}

/// <param name="Item">"vm:&lt;id&gt;" or "host".</param>
/// <param name="UploadedBytes">What the run added to the repository after dedup and compression; null when it was not measured.</param>
/// <param name="EncryptedDisks">How many of the VM's disks hold an encrypted volume; null for host state and older runs.</param>
public sealed record BackupRunRecord(
    Guid RunId,
    string Item,
    Guid? VmId,
    string? VmName,
    string TargetId,
    BackupTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    BackupOutcome Outcome,
    string? Reason,
    string? SnapshotId,
    long TotalBytes,
    IReadOnlyList<string> Warnings,
    long? UploadedBytes = null,
    int? EncryptedDisks = null);

/// <summary>Run history in backup-history.json, newest last, capped. Holds no secrets.</summary>
public sealed class BackupHistoryStore
{
    public const string FileName = "backup-history.json";
    public const int MaxRecords = 500;

    private readonly string _path;
    private readonly object _gate = new();

    public BackupHistoryStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public void Add(BackupRunRecord record)
    {
        lock (_gate)
        {
            var all = Read();
            all.Add(record);
            if (all.Count > MaxRecords)
            {
                all.RemoveRange(0, all.Count - MaxRecords);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(all, BackupJson.Options));
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<BackupRunRecord> List(int limit = 100)
    {
        lock (_gate)
        {
            return Read().AsEnumerable().Reverse().Take(limit).ToList();
        }
    }

    /// <summary>The latest run of any outcome, which is what decides whether a schedule slot was already served.</summary>
    public BackupRunRecord? Last(string item, string targetId)
    {
        lock (_gate)
        {
            return Read().LastOrDefault(r => r.Item == item && r.TargetId == targetId);
        }
    }

    public BackupRunRecord? LastSuccess(string item, string targetId)
    {
        lock (_gate)
        {
            return Read().LastOrDefault(r => r.Item == item && r.TargetId == targetId && r.Outcome is BackupOutcome.Succeeded or BackupOutcome.Partial);
        }
    }

    private List<BackupRunRecord> Read() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<List<BackupRunRecord>>(Utf8Json.ReadFile(_path), BackupJson.Options) ?? []
            : [];
}

/// <summary>A connected repository and its passphrase, ready for <see cref="KopiaCli"/> calls.</summary>
public sealed record OpenRepository(KopiaRepository Repository, string Passphrase)
{
    public override string ToString() => $"OpenRepository {{ {Repository.RepoId} }}";
}

public interface IBackupRepositories
{
    /// <exception cref="BackupException">The repository's recovery key was never confirmed, or its passphrase is missing.</exception>
    /// <exception cref="KopiaException">The storage could not be reached or refused the keys.</exception>
    Task<OpenRepository> OpenAsync(BackupTarget target, RepositoryRole role, CancellationToken cancellationToken);
}

/// <summary>
/// Opens a target's repository for a run. A repository whose recovery key was not confirmed cannot be opened. The first
/// open connects to an existing repository, or creates one when the storage holds none.
/// </summary>
public sealed class BackupRepositories(KopiaCli kopia, IBackupSecretStore secrets, string dataDirectory) : IBackupRepositories
{
    public async Task<OpenRepository> OpenAsync(BackupTarget target, RepositoryRole role, CancellationToken cancellationToken)
    {
        var reference = role == RepositoryRole.Vm ? target.VmRepository : target.HostRepository;
        if (reference is null || reference.KeyState != RepositoryKeyState.Confirmed)
        {
            throw new BackupException($"The backup target \"{target.Name}\" is not ready: its recovery key has not been confirmed.");
        }

        var passphrase = secrets.Get(RecoveryKeyService.PassphraseReference(reference.RepoId))
            ?? throw new BackupException($"The passphrase for \"{target.Name}\" is missing from this host. Restore it from your recovery key by setting the target up again.");
        var repository = KopiaBackendFactory.For(target, role, reference.RepoId, dataDirectory, secrets);
        if (target.Kind == BackupTargetKind.Local)
        {
            // Kopia does not create the storage folder, and the folder is where RESTORE.md belongs.
            Directory.CreateDirectory(Path.Combine(target.Local!.Path, KopiaBackendFactory.RoleFolder(role)));
            RestoreInstructionsWriter.WriteForLocal(target, DateTimeOffset.UtcNow);
        }

        if (!File.Exists(repository.ConfigFile))
        {
            try
            {
                await kopia.ConnectAsync(repository, passphrase, readOnly: false, cancellationToken).ConfigureAwait(false);
            }
            catch (KopiaException ex) when (ex.Failure == KopiaFailure.NotARepository)
            {
                await kopia.CreateRepositoryAsync(repository, passphrase, cancellationToken).ConfigureAwait(false);
            }
        }

        return new OpenRepository(repository, passphrase);
    }
}
