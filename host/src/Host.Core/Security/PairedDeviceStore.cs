using System.Text.Json;

namespace HyperHarbor.Host.Core.Security;

/// <summary>A client device allowed to call the API with mTLS.</summary>
/// <param name="CertificateFingerprint">SHA-256 of the client certificate DER, uppercase hex.</param>
public sealed record PairedDevice(Guid DeviceId, string Name, string CertificateFingerprint, DateTimeOffset PairedAt);

/// <summary>
/// Persists paired devices. Any change grants or revokes API access, so the file is written
/// with a restricted ACL.
/// </summary>
public sealed class PairedDeviceStore
{
    private const string FileName = "paired-devices.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<PairedDevice>? _devices;

    public PairedDeviceStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    /// <summary>Raised after a device is added or removed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PairedDevice> List()
    {
        lock (_gate)
        {
            return Devices().OrderBy(device => device.PairedAt).ToList();
        }
    }

    public PairedDevice? FindByFingerprint(string fingerprint)
    {
        lock (_gate)
        {
            return Devices().FirstOrDefault(device =>
                string.Equals(device.CertificateFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Adds a device. A device re-pairing with the same certificate replaces its old entry.</summary>
    public PairedDevice Add(string name, string fingerprint, DateTimeOffset pairedAt)
    {
        PairedDevice device;
        lock (_gate)
        {
            var devices = Devices();
            devices.RemoveAll(existing =>
                string.Equals(existing.CertificateFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            device = new PairedDevice(Guid.NewGuid(), name, fingerprint.ToUpperInvariant(), pairedAt);
            devices.Add(device);
            Save(devices);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return device;
    }

    public bool Remove(Guid deviceId)
    {
        lock (_gate)
        {
            var devices = Devices();
            if (devices.RemoveAll(device => device.DeviceId == deviceId) == 0)
            {
                return false;
            }

            Save(devices);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private List<PairedDevice> Devices()
    {
        if (_devices is null)
        {
            _devices = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(_path), JsonOptions)
                    ?? throw new InvalidDataException($"The paired device file '{_path}' is invalid.")
                : [];
        }

        return _devices;
    }

    private void Save(List<PairedDevice> devices)
    {
        ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(devices, JsonOptions));
    }
}
