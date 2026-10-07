using System.Security.Cryptography;
using System.Text;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.HostProfiles;

public sealed record ServiceChange(string Name, string DisplayName, string From, string To, bool StopNow);

public sealed record StartupChange(StartupEntry Entry, bool Enable);

/// <param name="BeforeType">dword, qword, string, or other; null when the value was absent. Read from the first hive (a signed-in user's, else Default).</param>
public sealed record RegistryChange(RegistryWrite Write, string? BeforeType, string? BeforeValue);

/// <param name="PlanFrom">The active plan's GUID when the plan changes.</param>
/// <param name="PlanTo">The wanted plan's GUID; null when the plan stays.</param>
/// <param name="Duplicate">The plan is missing and is created from Windows' built-in scheme under the same GUID.</param>
public sealed record PowerChange(
    string? PlanFrom,
    string? PlanFromName,
    string? PlanTo,
    string? PlanToName,
    bool Duplicate,
    IReadOnlyList<string> Disarm,
    IReadOnlyList<string> Arm);

public sealed record AppxChange(string Name, string FriendlyName);

public sealed record ProgramChange(HostProgramEntry Catalog, InstalledProgram Found);

/// <summary>
/// What a profile would change on the host right now: only the differences, so applying twice changes nothing the
/// second time. Also what was kept and why.
/// </summary>
/// <param name="Kept">Things the profile asked for that a guard or an allowlist kept, each with the reason.</param>
/// <param name="AlreadyInPlace">Items the profile covers that are already as wanted.</param>
public sealed record HostDiff(
    IReadOnlyList<ServiceChange> Services,
    IReadOnlyList<StartupChange> Startup,
    IReadOnlyList<RegistryChange> Registry,
    PowerChange? Power,
    IReadOnlyList<AppxChange> Appx,
    IReadOnlyList<ProgramChange> Programs,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Problems,
    int AlreadyInPlace)
{
    public static HostDiff Empty { get; } = new([], [], [], null, [], [], [], [], 0);

    public int ChangeCount =>
        Services.Count + Startup.Count + Registry.Count + Appx.Count + Programs.Count
        + (Power is null ? 0 : (Power.PlanTo is null ? 0 : 1) + Power.Disarm.Count + Power.Arm.Count);

    public bool IsEmpty => ChangeCount == 0;

    /// <summary>The changes as the tray lists them, in the order they are applied.</summary>
    public IReadOnlyList<HostLeanChangeLine> Lines()
    {
        var lines = new List<HostLeanChangeLine>();
        foreach (var change in Services.OrderBy(change => change.Name, StringComparer.OrdinalIgnoreCase))
        {
            var label = change.DisplayName.Length > 0 && !string.Equals(change.DisplayName, change.Name, StringComparison.OrdinalIgnoreCase) ? $"{change.DisplayName} ({change.Name})" : change.Name;
            lines.Add(new("services", label, $"Startup type {change.From} to {change.To}{(change.StopNow ? ", stop it now" : "")}"));
        }

        foreach (var change in Startup.OrderBy(change => change.Entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(change => change.Entry.Key, StringComparer.Ordinal))
        {
            lines.Add(new("startup", change.Entry.Name, $"{(change.Enable ? "Enable" : "Disable")} ({Describe(change.Entry)})"));
        }

        foreach (var change in Registry)
        {
            lines.Add(new("registry", change.Write.Item, $@"{(change.Write.Target == RegistryTarget.Machine ? "HKLM" : "HKCU")}\{change.Write.Key}\{change.Write.Name}: {Show(change.BeforeType, change.BeforeValue)} to {Show(change.Write.Type == "absent" ? null : change.Write.Type, change.Write.Value)}"));
        }

        if (Power is { } power)
        {
            if (power.PlanTo is not null)
            {
                lines.Add(new("power", "Power plan", $"{power.PlanFromName ?? power.PlanFrom ?? "unknown"} to {power.PlanToName ?? power.PlanTo}{(power.Duplicate ? " (created first)" : "")}"));
            }

            lines.AddRange(power.Disarm.Select(device => new HostLeanChangeLine("power", device, "May no longer wake the PC")));
            lines.AddRange(power.Arm.Select(device => new HostLeanChangeLine("power", device, "May wake the PC")));
        }

        lines.AddRange(Appx.Select(change => new HostLeanChangeLine("appx", change.FriendlyName.Length > 0 ? $"{change.FriendlyName} ({change.Name})" : change.Name, "Remove the app for every account and from new accounts")));
        lines.AddRange(Programs.Select(change => new HostLeanChangeLine("programs", change.Found.Name, change.Catalog.Uninstall == "oneDrive" ? "Uninstall (the OneDrive uninstaller)" : "Uninstall (its quiet uninstall command)")));
        return lines;
    }

    /// <summary>Identifies the changes: an apply is allowed only when a fresh look at the host gives the dry run's fingerprint.</summary>
    public string Fingerprint() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Lines().Select(line => $"{line.Handler}|{line.Item}|{line.Text}")))));

    private static string Describe(StartupEntry entry) =>
        $"{(entry.Scope == "machine" ? "all users" : "one user")}, {(entry.Source == "Folder" ? "Startup folder" : "Run key")}";

    private static string Show(string? type, string? value) => type switch
    {
        null => "absent",
        "other" => "another kind of value",
        "string" => $"\"{value}\"",
        _ => value ?? "",
    };
}
