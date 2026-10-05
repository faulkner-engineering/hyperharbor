using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Hosts;

/// <summary>Schema: HostUpdateMode.</summary>
public enum HostUpdateMode
{
    Auto,
    Notify,
    Off,
}

/// <summary>Schema: HostUpdateActivity.</summary>
public enum HostUpdateActivity
{
    Idle,
    Checking,
    Preparing,
    Ready,
    Installing,
}

/// <summary>The host's update status and settings. Schema: HostUpdateStatus.</summary>
/// <param name="Supported">False when the host runs without being installed; it then does not update itself.</param>
public sealed record HostUpdateStatus(
    bool Supported,
    HostUpdateMode Mode,
    string Channel,
    IReadOnlyList<string> Channels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MaintenanceTime,
    string CurrentVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AvailableVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NotesUrl,
    HostUpdateActivity Activity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastCheck,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastResult,
    IReadOnlyList<string> RolledBack);

/// <summary>Schema: HostUpdateSettings.</summary>
/// <param name="MaintenanceTime">HH:mm host local time, or null for none.</param>
public sealed record HostUpdateSettings(
    [property: JsonRequired] string Channel,
    [property: JsonRequired] HostUpdateMode Mode,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MaintenanceTime);
