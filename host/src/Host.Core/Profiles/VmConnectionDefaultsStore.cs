using System.Text.Json;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>The Remote Desktop defaults a machine profile gave a VM, sent to clients with its connection.</summary>
public sealed record VmConnectionDefaults(Guid VmId, MachineConnection Connection, string ProfileName, DateTimeOffset SetAt);

/// <summary>Remote Desktop defaults by VM, in connection-defaults.json with the ProtectedFile ACL. Holds no secrets.</summary>
public sealed class VmConnectionDefaultsStore
{
    public const string FileName = "connection-defaults.json";
    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.Options) { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<Guid, VmConnectionDefaults>? _records;

    public VmConnectionDefaultsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public VmConnectionDefaults? Find(Guid vmId)
    {
        lock (_gate)
        {
            return Records().GetValueOrDefault(vmId);
        }
    }

    public void Save(VmConnectionDefaults record)
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

    private Dictionary<Guid, VmConnectionDefaults> Records()
    {
        _records ??= File.Exists(_path)
            ? JsonSerializer.Deserialize<List<VmConnectionDefaults>>(File.ReadAllText(_path), JsonOptions)?.ToDictionary(record => record.VmId)
                ?? throw new InvalidDataException($"The connection defaults file '{_path}' is invalid.")
            : [];
        return _records;
    }

    private void Write() => ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(Records().Values.ToList(), JsonOptions));
}
