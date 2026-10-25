using System.Text.Json;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>Why an administrator window ended. Recorded in the audit trail.</summary>
public static class GuestAdminCloseReason
{
    public const string User = "user";
    public const string Expired = "expired";
    public const string VmStopped = "vmStopped";
    public const string HostStartup = "hostStartup";
    public const string OpenFailed = "openFailed";
}

/// <summary>
/// A window whose account may be enabled in the guest. Never holds the passphrase.
/// </summary>
/// <param name="DeviceId">The device the window was opened for; null when the host opened it.</param>
/// <param name="ClosePending">The window ended, and the account still has to be disabled in the guest.</param>
/// <param name="CloseReason">Why it ended, when <paramref name="ClosePending"/> is set.</param>
public sealed record GuestAdminRecord(
    Guid VmId,
    Guid UserId,
    string AccountName,
    DateTimeOffset ExpiresAt,
    Guid? DeviceId,
    string? DeviceName,
    bool ClosePending = false,
    string? CloseReason = null);

/// <summary>
/// guest-admin.json: the window length and the windows that may still be enabled in a guest, so a host that
/// restarts (or a VM that stopped) still gets its accounts disabled. Contains no secrets.
/// </summary>
public sealed class GuestAdminStore
{
    public const string FileName = "guest-admin.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Document? _document;

    public GuestAdminStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public int WindowMinutes
    {
        get
        {
            lock (_gate)
            {
                return Load().WindowMinutes ?? GuestAdminSettings.DefaultMinutes;
            }
        }
    }

    public void SetWindowMinutes(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, GuestAdminSettings.MinimumMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, GuestAdminSettings.MaximumMinutes);
        lock (_gate)
        {
            Load().WindowMinutes = minutes;
            Write();
        }
    }

    public IReadOnlyList<GuestAdminRecord> List()
    {
        lock (_gate)
        {
            return Load().Windows.ToList();
        }
    }

    public GuestAdminRecord? Find(Guid vmId, Guid userId)
    {
        lock (_gate)
        {
            return Load().Windows.FirstOrDefault(record => record.VmId == vmId && record.UserId == userId);
        }
    }

    public void Save(GuestAdminRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var windows = Load().Windows;
            windows.RemoveAll(existing => existing.VmId == record.VmId && existing.UserId == record.UserId);
            windows.Add(record);
            Write();
        }
    }

    public bool Remove(Guid vmId, Guid userId)
    {
        lock (_gate)
        {
            if (Load().Windows.RemoveAll(record => record.VmId == vmId && record.UserId == userId) == 0)
            {
                return false;
            }

            Write();
            return true;
        }
    }

    private Document Load()
    {
        _document ??= File.Exists(_path)
            ? JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException($"The administrator window file '{_path}' is invalid.")
            : new Document();
        return _document;
    }

    private void Write() => ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(_document, JsonOptions));

    private sealed class Document
    {
        public int? WindowMinutes { get; set; }

        public List<GuestAdminRecord> Windows { get; set; } = [];
    }
}
