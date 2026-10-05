using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Audit;

/// <summary>Stage of an audited operation.</summary>
public enum AuditOutcome
{
    /// <summary>Written before the operation runs. Nothing runs if this entry cannot be written.</summary>
    Requested,
    Succeeded,
    Failed,
}

/// <summary>
/// One audit log line. Never holds a secret: parameters are summarized in <see cref="Detail"/> by the
/// code that knows which values are safe.
/// </summary>
/// <param name="Action">The api.yaml operationId, or a tray action such as "trayRemoveDevice".</param>
/// <param name="Status">HTTP status of the response, when there is one.</param>
/// <param name="Elevated">True when the request carried a valid elevation token.</param>
public sealed record AuditEntry(
    DateTimeOffset Time,
    string Action,
    AuditOutcome Outcome,
    Guid? UserId = null,
    string? UserName = null,
    Guid? DeviceId = null,
    string? DeviceName = null,
    Guid? VmId = null,
    string? VmName = null,
    bool Elevated = false,
    int? Status = null,
    string? Detail = null,
    Guid? JobId = null);

/// <summary>Thrown when an audit entry cannot be written. The operation must not run.</summary>
public sealed class AuditUnavailableException : Exception
{
    public AuditUnavailableException(Exception inner)
        : base("The audit log cannot be written, so the operation was not performed.", inner)
    {
    }
}

/// <summary>Append-only record of who changed what on this host.</summary>
public interface IAuditLog
{
    /// <exception cref="AuditUnavailableException">The entry could not be written.</exception>
    void Write(AuditEntry entry);
}

/// <summary>
/// Writes audit entries as JSON lines to audit.log in the data directory. The file is created with
/// the restricted ACL from <see cref="ProtectedFile"/>. When it grows past <see cref="MaxBytes"/> it is
/// renamed to audit.1.log, replacing the previous one.
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    public const string FileName = "audit.log";
    public const string PreviousFileName = "audit.1.log";
    public const long DefaultMaxBytes = 10 * 1024 * 1024;

    /// <summary>Longest detail kept, in characters.</summary>
    public const int MaxDetailLength = 500;

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly string _path;
    private readonly string _previousPath;
    private readonly object _gate = new();
    private readonly IReadOnlyCollection<SecurityIdentifier> _readers;

    /// <param name="readers">Accounts that may also read the audit trail (see <see cref="ProtectedFile.OpenAppend"/>).</param>
    public FileAuditLog(string dataDirectory, long maxBytes = DefaultMaxBytes, IReadOnlyCollection<SecurityIdentifier>? readers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _readers = readers ?? [];
        _path = Path.Combine(dataDirectory, FileName);
        _previousPath = Path.Combine(dataDirectory, PreviousFileName);
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }

    public void Write(AuditEntry entry)
    {
        var line = Serialize(entry);
        try
        {
            lock (_gate)
            {
                RollOverIfFull();
                using var stream = ProtectedFile.OpenAppend(_path, _readers);
                stream.Write(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditUnavailableException(ex);
        }
    }

    /// <summary>One JSON object and a line feed. Control characters in the detail are removed and it is length capped.</summary>
    internal static byte[] Serialize(AuditEntry entry)
    {
        if (entry.Detail is { } detail)
        {
            var clean = new string(detail.Where(character => !char.IsControl(character)).ToArray());
            entry = entry with { Detail = clean.Length > MaxDetailLength ? clean[..MaxDetailLength] + "…" : clean };
        }

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
    }

    private void RollOverIfFull()
    {
        var file = new FileInfo(_path);
        if (file.Exists && file.Length >= MaxBytes)
        {
            File.Move(_path, _previousPath, overwrite: true);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
