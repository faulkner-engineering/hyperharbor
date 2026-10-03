using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Operating system family a guest reports through Hyper-V data exchange. Schema: GuestOsFamily.</summary>
public enum GuestOsFamily
{
    Unknown,
    Windows,
    Linux,
}

/// <summary>Guest operating system. Schema: VmGuestOs.</summary>
/// <param name="Name">Name and version the guest reports, for example "Ubuntu 24.04". Sent as null when unknown (required in the schema).</param>
public sealed record VmGuestOs(
    GuestOsFamily Family,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name)
{
    public static readonly VmGuestOs Unknown = new(GuestOsFamily.Unknown, null);
}
