using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>Checks Performance mode settings against the host and works out the GPU partition.</summary>
public static class PerformanceValidator
{
    public const int MinLowMmioGapMb = 128;
    public const int MaxLowMmioGapMb = 3584;
    public const int MinHighMmioGapMb = 1024;
    public const int MaxHighMmioGapMb = 512 * 1024;

    /// <summary>Fills in defaults and checks every value.</summary>
    /// <returns>The settings as they will be stored, and the plan for Hyper-V.</returns>
    /// <exception cref="LifecycleConflictException">The host has no partitionable GPU (code gpuUnavailable).</exception>
    /// <exception cref="LifecycleValidationException">A value is invalid; Errors lists each problem.</exception>
    /// <exception cref="ResourceWarningsException">Warnings were not acknowledged.</exception>
    public static (PerformanceSettings Settings, PerformancePlan Plan) Check(
        PerformanceSettings request,
        IReadOnlyList<HostGpuInfo> gpus,
        HostCapacity host,
        long memoryReserveMb)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        // Performance mode always uses fixed memory, so startup and maximum are the same value.
        var compute = new List<ValidationIssue>();
        VmSettingsValidator.Check(new ComputeRequest(request.ProcessorCount, request.MemoryMb, request.MemoryMb, false), host, memoryReserveMb, compute, warnings);
        errors.AddRange(compute.Select(Rename).DistinctBy(issue => issue.Field));
        warnings = warnings.Select(Rename).DistinctBy(issue => issue.Field).ToList();

        var partitionable = gpus.Where(gpu => gpu.Partitionable).ToList();
        if (partitionable.Count == 0)
        {
            throw new LifecycleConflictException(
                "This host has no GPU that Hyper-V can partition. Performance mode needs one (a recent NVIDIA, AMD, or Intel GPU with a WDDM 2.5 or later driver).",
                ContractInfo.ProblemCodes.GpuUnavailable);
        }

        var share = request.Gpu ?? new GpuPartitionShare();
        var gpu = share.InstancePath is { } path
            ? partitionable.FirstOrDefault(item => string.Equals(item.InstancePath, path, StringComparison.OrdinalIgnoreCase))
            : partitionable[0];
        if (gpu is null)
        {
            errors.Add(new("gpu.instancePath", "That GPU cannot be partitioned on this host. Choose one from the host's GPUs."));
        }

        foreach (var (field, percent) in new[]
        {
            ("gpu.vramPercent", share.VramPercent),
            ("gpu.encodePercent", share.EncodePercent),
            ("gpu.decodePercent", share.DecodePercent),
            ("gpu.computePercent", share.ComputePercent),
        })
        {
            if (percent is < 1 or > 100)
            {
                errors.Add(new(field, "Use a share from 1 to 100 percent."));
            }
        }

        var mmio = request.Mmio ?? new MmioSettings();
        if (mmio.LowGapMb is < MinLowMmioGapMb or > MaxLowMmioGapMb)
        {
            errors.Add(new("mmio.lowGapMb", $"Use {MinLowMmioGapMb} to {MaxLowMmioGapMb} MB."));
        }

        if (mmio.HighGapMb is < MinHighMmioGapMb or > MaxHighMmioGapMb)
        {
            errors.Add(new("mmio.highGapMb", $"Use {MinHighMmioGapMb} to {MaxHighMmioGapMb} MB."));
        }

        var move = string.IsNullOrWhiteSpace(request.MoveStorageTo) ? null : request.MoveStorageTo.Trim();
        if (move is not null && (!Path.IsPathFullyQualified(move) || !Directory.Exists(Path.GetPathRoot(move))))
        {
            errors.Add(new("moveStorageTo", "Use a full folder path on a drive the host has, for example D:\\Fast VMs\\Dev Box."));
        }

        if (errors.Count > 0)
        {
            throw new LifecycleValidationException(errors[0].Message, errors);
        }

        if (warnings.Count > 0 && !request.AcknowledgeWarnings)
        {
            throw new ResourceWarningsException(warnings);
        }

        var capacity = gpu!.Capacity!;
        var plan = new PerformancePlan(
            new GpuPartitionPlan(
                share.InstancePath is null ? null : gpu.InstancePath,
                GpuIdentity.Share(capacity.Vram, share.VramPercent),
                GpuIdentity.Share(capacity.Encode, share.EncodePercent),
                GpuIdentity.Share(capacity.Decode, share.DecodePercent),
                GpuIdentity.Share(capacity.Compute, share.ComputePercent)),
            mmio.LowGapMb,
            mmio.HighGapMb);
        var settings = request with
        {
            Gpu = share with { InstancePath = gpu.InstancePath },
            Mmio = mmio,
            MoveStorageTo = move,
            Rdp = request.Rdp ?? new PerformanceRdpSettings(),
            AcknowledgeWarnings = false,
        };
        return (settings, plan);
    }

    /// <summary>The compute validator names startup and maximum memory; Performance mode has one memory value.</summary>
    private static ValidationIssue Rename(ValidationIssue issue) =>
        issue.Field is "startupMemoryMb" or "maximumMemoryMb" ? issue with { Field = "memoryMb" } : issue;
}
