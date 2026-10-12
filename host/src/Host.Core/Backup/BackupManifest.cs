using System.Text.Json;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Backup;

public enum ConsistencyMode
{
    Off,
    Production,
    Standard,
    Live,
}

public sealed record ManifestVm(Guid Id, string Name, int Generation, string? Notes);

public sealed record ManifestCompute(
    int VCpu, long StartupMemoryMb, long? MaxMemoryMb, bool DynamicMemory, bool NestedVirtualization, bool MacAddressSpoofing);

public sealed record ManifestFirmware(bool SecureBoot, string? SecureBootTemplateId);

public sealed record ManifestAdapter(string? SwitchName, string? SwitchId, string? MacAddress);

/// <param name="File">The file's name inside the snapshot.</param>
public sealed record ManifestDisk(string File, string ControllerType, int Controller, int Lun, long SizeBytes);

public sealed record ManifestGpu(ulong Vram, ulong Encode, ulong Decode, ulong Compute, int LowMmioGapMb, int HighMmioGapMb);

public sealed record ManifestPerformance(bool Applied, PerformanceSettings? Settings, string? GuestDriverVersion);

public sealed record ManifestConsistency(ConsistencyMode Mode, IReadOnlyList<string> Warnings);

public sealed record ManifestGuest(string? Os, bool Provisioned);

/// <summary>
/// manifest.json inside every VM snapshot: what a restore needs to define the VM again. Readers ignore fields they
/// do not know; a newer schema version than this host understands is refused.
/// </summary>
public sealed record BackupManifest
{
    public const int CurrentSchema = 1;
    public const string FileName = "manifest.json";

    public int SchemaVersion { get; init; } = CurrentSchema;

    public required string HyperHarborVersion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required ManifestVm Vm { get; init; }

    public required ManifestCompute Compute { get; init; }

    public required ManifestFirmware Firmware { get; init; }

    public bool Tpm { get; init; }

    public IReadOnlyList<ManifestAdapter> Network { get; init; } = [];

    public IReadOnlyList<ManifestDisk> Disks { get; init; } = [];

    public ManifestGpu? Gpu { get; init; }

    public required ManifestPerformance Performance { get; init; }

    public required ManifestConsistency Consistency { get; init; }

    public required ManifestGuest Guest { get; init; }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, BackupJson.Options);

    /// <exception cref="InvalidDataException">The text is not a manifest, or its schema is newer than this host understands.</exception>
    public static BackupManifest Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            // A newer schema may have changed required fields, so its version is checked before the full read.
            using (var document = JsonDocument.Parse(json.ToArray()))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("schemaVersion", out var version)
                    && version.TryGetInt32(out var found) && found > CurrentSchema)
                {
                    throw new InvalidDataException(
                        $"This backup was made by a newer HyperHarbor (manifest version {found}); this host reads version {CurrentSchema} and earlier. Update the host to restore it.");
                }
            }

            return JsonSerializer.Deserialize<BackupManifest>(json, BackupJson.Options)
                ?? throw new InvalidDataException("The backup's manifest.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup's manifest.json is not valid.", ex);
        }
    }
}

/// <summary>Builds the manifest from what a backup run learned. Pure.</summary>
public static class ManifestBuilder
{
    /// <param name="diskFiles">The names of the staged disk files, in the order of <see cref="VmDefinition.Disks"/>.</param>
    /// <param name="diskSizes">The size of each staged disk file, same order.</param>
    public static BackupManifest Build(
        VmDefinition definition,
        IReadOnlyList<string> diskFiles,
        IReadOnlyList<long> diskSizes,
        PerformanceRecord? performance,
        ConsistencyMode mode,
        IReadOnlyList<string> warnings,
        string? guestOs,
        bool provisioned,
        string version,
        DateTimeOffset now)
    {
        if (diskFiles.Count != definition.Disks.Count || diskSizes.Count != diskFiles.Count)
        {
            throw new ArgumentException("There must be one staged file and size per disk in the definition.");
        }

        return new BackupManifest
        {
            HyperHarborVersion = version,
            CreatedAt = now,
            Vm = new ManifestVm(definition.VmId, definition.Name, definition.Generation, definition.Notes),
            Compute = new ManifestCompute(
                definition.Compute.ProcessorCount,
                definition.Compute.StartupMemoryMb,
                definition.Compute.DynamicMemory ? definition.Compute.MaximumMemoryMb : null,
                definition.Compute.DynamicMemory,
                definition.Compute.NestedVirtualization,
                definition.Compute.MacAddressSpoofing),
            Firmware = new ManifestFirmware(definition.SecureBoot, definition.SecureBootTemplateId),
            Tpm = definition.TpmEnabled,
            Network = definition.Adapters.Select(a => new ManifestAdapter(a.SwitchName, a.SwitchId, a.MacAddress)).ToList(),
            Disks = definition.Disks.Select((disk, i) => new ManifestDisk(diskFiles[i], disk.ControllerType, disk.ControllerNumber, disk.Lun, diskSizes[i])).ToList(),
            Gpu = definition.Gpu is { } gpu
                ? new ManifestGpu(gpu.Vram, gpu.Encode, gpu.Decode, gpu.Compute, definition.LowMmioGapMb, definition.HighMmioGapMb)
                : null,
            Performance = new ManifestPerformance(performance is not null, performance?.Settings, performance?.Guest?.DriverVersion),
            Consistency = new ManifestConsistency(mode, warnings),
            Guest = new ManifestGuest(guestOs, provisioned),
        };
    }
}
