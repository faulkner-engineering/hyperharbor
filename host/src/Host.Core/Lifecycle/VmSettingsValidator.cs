using System.Globalization;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Processor and memory settings to check against the host.</summary>
/// <param name="StartupMemoryMb">Memory the VM is given at start (all of it with static memory).</param>
public sealed record ComputeRequest(int ProcessorCount, long StartupMemoryMb, long MaximumMemoryMb, bool DynamicMemory, bool NestedVirtualization = false);

/// <summary>
/// Checks processor and memory settings against the host's capacity. Errors make a request invalid;
/// warnings (the host would be left short of memory) need the client to acknowledge them.
/// </summary>
public static class VmSettingsValidator
{
    /// <summary>Hyper-V requires memory in multiples of 2 MB.</summary>
    public const int MemoryAlignmentMb = 2;
    public const long MinimumMemoryMb = 32;

    /// <summary>The largest memory Hyper-V assigns a Generation 2 VM (12 TB).</summary>
    public const long MaximumMemoryLimitMb = 12L * 1024 * 1024;

    /// <summary>Dynamic memory never takes a VM below this, or below its startup memory if that is smaller.</summary>
    public const long DynamicMinimumMb = 512;

    public static void Check(
        ComputeRequest request,
        HostCapacity host,
        long reserveMb,
        List<ValidationIssue> errors,
        List<ValidationIssue> warnings)
    {
        if (request.ProcessorCount < 1 || request.ProcessorCount > host.LogicalProcessorCount)
        {
            errors.Add(new("processorCount", $"Use 1 to {host.LogicalProcessorCount} virtual processors; this host has {host.LogicalProcessorCount} logical processors."));
        }

        CheckMemory("startupMemoryMb", request.StartupMemoryMb, errors);
        if (request.StartupMemoryMb > host.TotalMemoryMb)
        {
            errors.Add(new("startupMemoryMb", $"The host has {Mb(host.TotalMemoryMb)} of memory in total."));
        }

        if (request.DynamicMemory)
        {
            CheckMemory("maximumMemoryMb", request.MaximumMemoryMb, errors);
            if (request.MaximumMemoryMb < request.StartupMemoryMb)
            {
                errors.Add(new("maximumMemoryMb", "Maximum memory must be at least the startup memory."));
            }
            else if (request.MaximumMemoryMb > host.TotalMemoryMb)
            {
                warnings.Add(new("maximumMemoryMb", $"Maximum memory is more than the host's {Mb(host.TotalMemoryMb)}; the VM can never use all of it."));
            }
        }

        if (request.NestedVirtualization && request.DynamicMemory)
        {
            errors.Add(new("dynamicMemory", "Nested virtualization needs static memory. Turn dynamic memory off."));
        }

        var left = host.AvailableMemoryMb - request.StartupMemoryMb;
        if (left < reserveMb)
        {
            warnings.Add(new("startupMemoryMb",
                $"Starting this VM would leave the host about {Mb(Math.Max(0, left))} free, below the reserve of {Mb(reserveMb)} ({Mb(host.AvailableMemoryMb)} is free now)."));
        }
    }

    private static void CheckMemory(string field, long valueMb, List<ValidationIssue> errors)
    {
        if (valueMb < MinimumMemoryMb || valueMb > MaximumMemoryLimitMb)
        {
            errors.Add(new(field, $"Use between {MinimumMemoryMb} MB and {Mb(MaximumMemoryLimitMb)}."));
        }
        else if (valueMb % MemoryAlignmentMb != 0)
        {
            errors.Add(new(field, "Use an even number of megabytes."));
        }
    }

    internal static string Mb(long megabytes) =>
        megabytes >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024.0:0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{megabytes} MB");
}
