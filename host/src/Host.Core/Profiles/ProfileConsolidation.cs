using System.Text.Json;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>What happened to one older profile when it was converted. Error is set when it was not.</summary>
public sealed record ConvertedProfile(Guid UserId, string Kind, string SourceId, string SourceName, string? MachineProfileId, string? Error);

/// <summary>
/// Makes machine profiles the one place for profiles (data format 4): each User's unattended install profiles become
/// machine profiles with only the account section, and their setup profiles machine profiles with only the software
/// sections. The older files stay as they are, so older clients still find them; this host's clients no longer show
/// them. What was converted, and what could not be, is recorded in machine-profiles\converted.json.
/// </summary>
public static class ProfileConsolidation
{
    public const string RecordFileName = "converted.json";
    public const string InstallKind = "install";
    public const string SetupKind = "setup";

    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.Options) { WriteIndented = true };

    /// <summary>Converts every User's older profiles in the data directory. A profile that does not convert is recorded and skipped.</summary>
    public static IReadOnlyList<ConvertedProfile> Run(string dataDirectory)
    {
        var unattend = new UnattendProfileStore(dataDirectory);
        var setup = new SetupProfileStore(dataDirectory, Catalogs.Default);
        var machines = new MachineProfileStore(dataDirectory, new MachineProfileValidator(new ProfileValidator(Catalogs.Default), unattend));
        var results = new List<ConvertedProfile>();

        foreach (var (userId, profile) in unattend.AllStored())
        {
            results.Add(Convert(machines, userId, InstallKind, profile.Id, profile.Name, () => FromInstall(profile)));
        }

        foreach (var userId in setup.Users())
        {
            foreach (var summary in setup.List(userId))
            {
                results.Add(summary.Error is { } error
                    ? new ConvertedProfile(userId, SetupKind, summary.Id, summary.Name, null, error)
                    : Convert(machines, userId, SetupKind, summary.Id, summary.Name, () => FromSetup(setup.Get(userId, summary.Id).Profile)));
            }
        }

        if (results.Count > 0)
        {
            var folder = Path.Combine(dataDirectory, MachineProfileStore.FolderName);
            Directory.CreateDirectory(folder);
            ProtectedFile.WriteAllBytes(Path.Combine(folder, RecordFileName), JsonSerializer.SerializeToUtf8Bytes(results, JsonOptions));
        }

        return results;
    }

    /// <summary>An unattended install profile as a machine profile with only the account section.</summary>
    public static MachineProfile FromInstall(UnattendProfile profile) => new(
        profile.Name,
        profile.Os,
        Account: new MachineAccount(profile.AdminAccountName, profile.TimeZone, profile.Locale, null, profile.Windows, profile.Linux));

    /// <summary>A setup profile as a Windows machine profile with only the software sections.</summary>
    public static MachineProfile FromSetup(SetupProfile profile) => new(
        profile.Name,
        InstallOs.Windows,
        profile.Description,
        Install: profile.Install,
        Remove: profile.Remove,
        Tweaks: profile.Tweaks,
        Browser: profile.Browser);

    private static ConvertedProfile Convert(MachineProfileStore machines, Guid userId, string kind, string sourceId, string sourceName, Func<MachineProfile> build)
    {
        try
        {
            var profile = build();
            var taken = machines.List(userId).Select(summary => summary.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (taken.Contains(profile.Name))
            {
                var suffix = kind == InstallKind ? " (install profile)" : " (setup profile)";
                var stem = profile.Name.Length + suffix.Length > ProfileValidator.MaxName
                    ? profile.Name[..(ProfileValidator.MaxName - suffix.Length)].TrimEnd()
                    : profile.Name;
                profile = profile with { Name = stem + suffix };
            }

            var saved = machines.Create(userId, profile);
            return new ConvertedProfile(userId, kind, sourceId, sourceName, saved.Id, null);
        }
        catch (Exception ex) when (ex is LifecycleValidationException or IOException or InvalidDataException or SetupProfileNotFoundException)
        {
            return new ConvertedProfile(userId, kind, sourceId, sourceName, null, ex.Message);
        }
    }
}
