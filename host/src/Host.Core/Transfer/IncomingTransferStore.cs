using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>One file of an incoming transfer.</summary>
/// <param name="Key">"disk:&lt;index&gt;" for a disk in the plan, "definition:&lt;file name&gt;" for an exported definition file.</param>
/// <param name="Path">Where the finished file goes; it is written to the same path plus ".partial" until it is complete.</param>
/// <param name="Received">Bytes received and verified; always a whole number of blocks, or the whole file.</param>
public sealed record IncomingFile(string Key, string Path, long Size, long Received)
{
    public bool IsComplete => Received == Size;
}

/// <summary>
/// What this host keeps about an incoming transfer so that a restart can roll it back or finish it. Holds no
/// secret: the ticket lives only in memory, as a hash.
/// </summary>
/// <param name="StagingFolder">The exported definition files; inside the VM's own folder, and deleted once the VM is imported.</param>
/// <param name="PlannedId">The planned (then realized) VM, once Hyper-V has imported it.</param>
/// <param name="OriginalStartupAction">The VM's automatic start action, put back when the transfer completes.</param>
public sealed record IncomingTransferRecord(
    Guid Id,
    Guid UserId,
    TransferMode Mode,
    string SourceFingerprint,
    TransferPreview Preview,
    TransferPlan Plan,
    string StagingFolder,
    IncomingTransferState State,
    IReadOnlyList<IncomingFile> Files,
    DateTimeOffset CreatedAt,
    Guid? PlannedId = null,
    ushort? OriginalStartupAction = null);

/// <summary>Incoming transfers in transfers.json, written through <see cref="ProtectedFile"/>.</summary>
public sealed class IncomingTransferStore
{
    public const string FileName = "transfers.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<IncomingTransferRecord>? _records;

    public IncomingTransferStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public IReadOnlyList<IncomingTransferRecord> List()
    {
        lock (_gate)
        {
            return Records().ToList();
        }
    }

    public IncomingTransferRecord? Find(Guid id)
    {
        lock (_gate)
        {
            return Records().FirstOrDefault(record => record.Id == id);
        }
    }

    public void Save(IncomingTransferRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var records = Records();
            records.RemoveAll(existing => existing.Id == record.Id);
            records.Add(record);
            Write(records);
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            var records = Records();
            if (records.RemoveAll(existing => existing.Id == id) > 0)
            {
                Write(records);
            }
        }
    }

    private void Write(List<IncomingTransferRecord> records) =>
        ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(records, Json));

    private List<IncomingTransferRecord> Records() =>
        _records ??= File.Exists(_path)
            ? JsonSerializer.Deserialize<List<IncomingTransferRecord>>(Utf8Json.ReadFile(_path), Json)
                ?? throw new InvalidDataException($"The transfer file '{_path}' is invalid.")
            : [];
}
