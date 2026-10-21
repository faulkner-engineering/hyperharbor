using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>Where an outgoing transfer is, as far as a restart needs to know.</summary>
public enum OutgoingTransferState
{
    /// <summary>Checking, exporting, or sending; the other host rolls its side back on its own.</summary>
    Sending,

    /// <summary>The other host has the VM. For a move, this host's copy is still to be deleted.</summary>
    Imported,
}

/// <summary>
/// What the sending host keeps about a transfer in progress. <see cref="OriginalKeyProtector"/> is set only while the
/// other host's guardian is granted on the VM's key protector, so a restart in that window can take the grant back. The
/// key protector holds no key in the clear (it is the same blob the VM's own files keep). The ticket is never stored.
/// </summary>
public sealed record OutgoingTransferRecord(
    Guid TransferId,
    Guid VmId,
    string VmName,
    TransferMode Mode,
    Guid TargetHostId,
    OutgoingTransferState State,
    DateTimeOffset StartedAt,
    byte[]? OriginalKeyProtector = null,
    string? PeerGuardianName = null);

/// <summary>Outgoing transfers in outgoing-transfers.json, written through <see cref="ProtectedFile"/>.</summary>
public sealed class OutgoingTransferStore
{
    public const string FileName = "outgoing-transfers.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<OutgoingTransferRecord>? _records;

    public OutgoingTransferStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public IReadOnlyList<OutgoingTransferRecord> List()
    {
        lock (_gate)
        {
            return Records().ToList();
        }
    }

    public void Save(OutgoingTransferRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var records = Records();
            records.RemoveAll(existing => existing.TransferId == record.TransferId);
            records.Add(record);
            Write(records);
        }
    }

    public void Remove(Guid transferId)
    {
        lock (_gate)
        {
            var records = Records();
            if (records.RemoveAll(existing => existing.TransferId == transferId) > 0)
            {
                Write(records);
            }
        }
    }

    private void Write(List<OutgoingTransferRecord> records) =>
        ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(records, Json));

    private List<OutgoingTransferRecord> Records() =>
        _records ??= File.Exists(_path)
            ? JsonSerializer.Deserialize<List<OutgoingTransferRecord>>(Utf8Json.ReadFile(_path), Json)
                ?? throw new InvalidDataException($"The transfer file '{_path}' is invalid.")
            : [];
}
