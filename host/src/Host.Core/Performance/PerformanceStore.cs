using System.Text.Json;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>The GPU driver copied into a guest by the last guest setup.</summary>
/// <param name="DriverVersion">The host driver version at the time, for drift detection.</param>
/// <param name="DriverFolders">The DriverStore FileRepository folders copied into HostDriverStore.</param>
public sealed record GuestDriverRecord(
    GpuVendor Vendor,
    string DriverVersion,
    IReadOnlyList<string> DriverFolders,
    DateTimeOffset CopiedAt,
    bool RebootRequired);

/// <summary>Performance mode as applied to one VM.</summary>
public sealed record PerformanceRecord(Guid VmId, PerformanceSettings Settings, DateTimeOffset AppliedAt, GuestDriverRecord? Guest = null);

/// <summary>Performance mode records by VM, in performance.json with the ProtectedFile ACL. Holds no secrets.</summary>
public sealed class PerformanceStore
{
    public const string FileName = "performance.json";
    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.Options) { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<Guid, PerformanceRecord>? _records;

    public PerformanceStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public PerformanceRecord? Find(Guid vmId)
    {
        lock (_gate)
        {
            return Records().GetValueOrDefault(vmId);
        }
    }

    public IReadOnlyList<PerformanceRecord> List()
    {
        lock (_gate)
        {
            return Records().Values.ToList();
        }
    }

    public void Save(PerformanceRecord record)
    {
        lock (_gate)
        {
            Records()[record.VmId] = record;
            Write();
        }
    }

    public bool Remove(Guid vmId)
    {
        lock (_gate)
        {
            if (!Records().Remove(vmId))
            {
                return false;
            }

            Write();
            return true;
        }
    }

    private Dictionary<Guid, PerformanceRecord> Records()
    {
        if (_records is null)
        {
            _records = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<PerformanceRecord>>(File.ReadAllText(_path), JsonOptions)?.ToDictionary(record => record.VmId)
                    ?? throw new InvalidDataException($"The performance file '{_path}' is invalid.")
                : [];
        }

        return _records;
    }

    private void Write() => ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(Records().Values.ToList(), JsonOptions));
}
