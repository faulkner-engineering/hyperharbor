using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Profiles;

/// <summary>How safe a provisioned package is to remove, from HyperHarbor's Appx catalog. Schema: AppxRating.</summary>
public enum AppxRating
{
    /// <summary>Not in the catalog.</summary>
    Unrated,

    /// <summary>Nothing else depends on it; part of the debloat preset.</summary>
    Safe,

    /// <summary>Removable, but people often expect it or a feature uses it.</summary>
    Caution,

    /// <summary>A framework, the Store, or a security piece; removing it breaks other things.</summary>
    Keep,
}

/// <summary>Schema: AppxBaselineSource.</summary>
public enum AppxBaselineSource
{
    /// <summary>Recorded when an unattended Windows install finished.</summary>
    UnattendedInstall,

    /// <summary>Recorded on request from a VM the owner knows is clean.</summary>
    Manual,
}

/// <summary>The clean list of provisioned packages a VM is compared with. Schema: AppxBaselineInfo.</summary>
/// <param name="Key">Major build and edition, for example 10.0.26100/Professional.</param>
/// <param name="Approximate">The baseline is for the same build but another edition.</param>
public sealed record AppxBaselineInfo(
    string Key,
    string Build,
    string Edition,
    DateTimeOffset RecordedAt,
    AppxBaselineSource Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? VmName,
    bool Approximate);

/// <summary>A provisioned Appx package. Schema: AppxPackage.</summary>
/// <param name="Name">The DisplayName from Get-AppxProvisionedPackage; what profiles list under remove.appx.</param>
/// <param name="InBaseline">Whether the clean baseline has it; null without a baseline.</param>
public sealed record AppxPackage(
    string Name,
    string FriendlyName,
    string Version,
    string Publisher,
    AppxRating Rating,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? InBaseline);

/// <summary>The provisioned packages in a VM, compared with the clean baseline. Schema: VmAppxInventory.</summary>
/// <param name="Build">The full build, for example 10.0.26100.2033.</param>
/// <param name="Edition">EditionID, for example Professional.</param>
/// <param name="Baseline">Null when no baseline exists for this build.</param>
/// <param name="RemovedFromBaseline">In the baseline but no longer in the VM: what someone removed.</param>
public sealed record VmAppxInventory(
    Guid VmId,
    string Build,
    string Edition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AppxBaselineInfo? Baseline,
    IReadOnlyList<AppxPackage> Packages,
    IReadOnlyList<AppxPackage> RemovedFromBaseline);
