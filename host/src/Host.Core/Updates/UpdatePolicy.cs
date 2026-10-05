using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.Updates;

public enum UpdateDecisionKind
{
    /// <summary>The offered version is not newer. Downgrades are never offered.</summary>
    UpToDate,

    /// <summary>A newer version may be downloaded and installed.</summary>
    Available,

    /// <summary>The release must be installed from a newer version than this one (minimumUpdateFrom).</summary>
    NeedsIntermediateVersion,

    /// <summary>This version was rolled back on this host before; it is skipped until a newer one appears.</summary>
    Skipped,
}

public sealed record UpdateDecision(UpdateDecisionKind Kind, SemanticVersion Current, SemanticVersion Offered, string Detail);

/// <summary>Decides whether a manifest's release should be installed over the running version.</summary>
public static class UpdatePolicy
{
    public static UpdateDecision Evaluate(UpdateManifest manifest, SemanticVersion current, IReadOnlyCollection<SemanticVersion> rolledBack)
    {
        var offered = manifest.ParsedVersion;
        if (offered <= current)
        {
            return new UpdateDecision(UpdateDecisionKind.UpToDate, current, offered, $"Version {current} is up to date.");
        }

        if (rolledBack.Contains(offered))
        {
            return new UpdateDecision(UpdateDecisionKind.Skipped, current, offered, $"Version {offered} failed to start here before and was rolled back.");
        }

        if (manifest.MinimumUpdateFrom is { } text && SemanticVersion.Parse(text) is var minimum && current < minimum)
        {
            return new UpdateDecision(UpdateDecisionKind.NeedsIntermediateVersion, current, offered,
                $"Version {offered} must be installed from version {minimum} or later. Install {minimum} first.");
        }

        return new UpdateDecision(UpdateDecisionKind.Available, current, offered, $"Version {offered} is available.");
    }
}
