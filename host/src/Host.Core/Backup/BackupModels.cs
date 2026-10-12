using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>The kinds of backup target. Rclone remotes are a later addition.</summary>
public enum BackupTargetKind
{
    Local,
    S3,
    Azure,
}

/// <summary>Whether the user has saved the repository's recovery key.</summary>
public enum RepositoryKeyState
{
    /// <summary>The key was generated but not confirmed; the repository does not exist and cannot run.</summary>
    PendingKey,
    Confirmed,
}

/// <summary>What a repository holds. VMs and host state are kept apart so host state restores on a fresh PC alone.</summary>
public enum RepositoryRole
{
    Vm,
    Host,
}

public sealed record LocalTargetSettings(string Path);

/// <summary>S3-compatible storage. The keys are held in the <see cref="BackupSecretStore"/> under the given references.</summary>
public sealed record S3TargetSettings(
    string Endpoint,
    string Bucket,
    string? Region,
    string? Prefix,
    bool UseTls,
    string AccessKeyRef,
    string SecretKeyRef);

public sealed record AzureTargetSettings(string Account, string Container, string? Prefix, string KeyRef);

public sealed record RepositoryRef(string RepoId, RepositoryKeyState KeyState);

/// <summary>Kopia retention. The defaults are 7 daily, 4 weekly, 6 monthly.</summary>
public sealed record RetentionPolicy(int Daily = 7, int Weekly = 4, int Monthly = 6)
{
    public static RetentionPolicy Default { get; } = new();
}

/// <summary>A local time of day on the listed days (all days when null).</summary>
public sealed record ScheduleSpec(TimeOnly Time, IReadOnlyList<DayOfWeek>? Days = null)
{
    public static ScheduleSpec Nightly { get; } = new(new TimeOnly(2, 0));
}

public sealed record TimeWindow(TimeOnly Start, TimeOnly End);

/// <summary>Conditions that must hold for a scheduled or shutdown-triggered run to start.</summary>
public sealed record RunConditions(bool NoActiveSession = true, bool NotInGame = true, TimeWindow? Window = null)
{
    public static RunConditions Default { get; } = new();
}

public enum GpuLiveCopy
{
    /// <summary>Skip a running GPU-P VM; back it up only when off.</summary>
    RequireOff,

    /// <summary>Copy the running VM's disk (crash consistent) and record a warning.</summary>
    AllowLive,
}

public sealed record BackupTarget
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required BackupTargetKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    public LocalTargetSettings? Local { get; init; }

    public S3TargetSettings? S3 { get; init; }

    public AzureTargetSettings? Azure { get; init; }

    public RepositoryRef? VmRepository { get; init; }

    public RepositoryRef? HostRepository { get; init; }

    public ScheduleSpec Schedule { get; init; } = ScheduleSpec.Nightly;

    public RetentionPolicy DefaultRetention { get; init; } = RetentionPolicy.Default;

    public int? DefaultBandwidthKbps { get; init; }

    public RunConditions DefaultConditions { get; init; } = RunConditions.Default;

    /// <summary>The kind must have its settings and no other kind's.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("The target needs a name.");
        }

        switch (Kind)
        {
            case BackupTargetKind.Local when Local is null || string.IsNullOrWhiteSpace(Local.Path):
                problems.Add("A local target needs a folder.");
                break;
            case BackupTargetKind.S3 when S3 is null || string.IsNullOrWhiteSpace(S3.Endpoint) || string.IsNullOrWhiteSpace(S3.Bucket):
                problems.Add("An S3 target needs an endpoint and a bucket.");
                break;
            case BackupTargetKind.Azure when Azure is null || string.IsNullOrWhiteSpace(Azure.Account) || string.IsNullOrWhiteSpace(Azure.Container):
                problems.Add("An Azure target needs a storage account and a container.");
                break;
        }

        if (DefaultRetention.Daily < 0 || DefaultRetention.Weekly < 0 || DefaultRetention.Monthly < 0
            || DefaultRetention.Daily + DefaultRetention.Weekly + DefaultRetention.Monthly == 0)
        {
            problems.Add("Retention needs at least one snapshot to keep.");
        }

        if (DefaultBandwidthKbps is <= 0)
        {
            problems.Add("The bandwidth limit must be positive.");
        }

        return problems;
    }
}

public sealed record BackupTriggers(bool Schedule = true, bool OnShutdown = false, int DebounceMinutes = 10);

/// <summary>Per VM and target.</summary>
public sealed record BackupPolicy
{
    public required string VmId { get; init; }

    public required string TargetId { get; init; }

    public bool Enabled { get; init; } = true;

    public RetentionPolicy Retention { get; init; } = RetentionPolicy.Default;

    public int? BandwidthKbps { get; init; }

    public RunConditions Conditions { get; init; } = RunConditions.Default;

    public BackupTriggers Triggers { get; init; } = new();

    public GpuLiveCopy GpuLiveCopy { get; init; } = GpuLiveCopy.RequireOff;
}

/// <summary>JSON options for the backup stores and manifest: camelCase, enums as strings, unknown fields ignored.</summary>
public static class BackupJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new TimeOnlyConverter());
        return options;
    }

    private sealed class TimeOnlyConverter : JsonConverter<TimeOnly>
    {
        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TimeOnly.ParseExact(reader.GetString()!, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }
}
