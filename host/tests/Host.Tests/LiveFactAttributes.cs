namespace HyperHarbor.Host.Tests;

/// <summary>
/// A fact that runs only when every named environment variable is set, so opt-in live tests show
/// as skipped rather than passing without doing anything.
/// </summary>
public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(params string[] variables)
    {
        var missing = variables.Where(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToList();
        if (missing.Count > 0)
        {
            Skip = $"Set {string.Join(", ", missing)} to run this live test.";
        }
    }
}

/// <summary>
/// A fact that reads this machine's hardware and expects a physical network adapter, which CI
/// virtual machines may not have. Skipped when the CI environment variable is set.
/// </summary>
public sealed class LocalHardwareFactAttribute : FactAttribute
{
    public LocalHardwareFactAttribute()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            Skip = "Reads local network hardware; skipped in CI.";
        }
    }
}
