using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

public sealed class MachineProfileTemplateNotFoundException(string id) : Exception($"There is no machine profile template \"{id}\".");

/// <summary>
/// The read-only starting points for machine profiles: the built-in unattended install profiles, each as a machine
/// profile with only the account section, with the host's time zone.
/// </summary>
public sealed class MachineProfileTemplates(UnattendProfileStore unattend)
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["windows-workstation"] = "Windows 11 Pro with the hardware checks Windows Setup makes.",
        ["windows-burner"] = "Windows 11 Pro that skips the TPM, Secure Boot, and memory checks, for short-lived VMs.",
        ["ubuntu-dev-server"] = "Ubuntu with a desktop, git, build-essential, and curl.",
    };

    public IReadOnlyList<MachineProfileTemplate> List() =>
        unattend.BuiltIns()
            .Select(builtIn => new MachineProfileTemplate(
                builtIn.Id,
                builtIn.Name,
                Descriptions.GetValueOrDefault(builtIn.Id, builtIn.Name),
                ProfileConsolidation.FromInstall(builtIn)))
            .ToList();

    /// <exception cref="MachineProfileTemplateNotFoundException">No such template.</exception>
    public MachineProfileTemplate Get(string id) =>
        List().FirstOrDefault(template => template.Id == id) ?? throw new MachineProfileTemplateNotFoundException(id);
}
