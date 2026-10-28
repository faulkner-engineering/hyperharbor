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
/// An end-to-end test that needs no Hyper-V: it runs the real host, kopia, or another local service, so it belongs in
/// CI. Runs when HH_E2E=1 (scripts\e2e.ps1 sets it, with the tools it needs); skipped otherwise so the quick test run
/// stays quick. Mark the class [Trait("Category", "E2E")] so the script finds it.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HH_E2E") != "1")
        {
            Skip = @"An end-to-end test; scripts\e2e.ps1 runs it (HH_E2E=1).";
        }
    }
}

/// <summary>What an end-to-end test needs from scripts\e2e.ps1.</summary>
public static class E2E
{
    /// <summary>A variable the script sets. Missing, the test fails rather than skips, so CI cannot pass without it.</summary>
    public static string Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($@"{name} is not set. Run the end-to-end tests through scripts\e2e.ps1.");

    /// <summary>The pinned kopia.exe that scripts\fetch-tools.ps1 downloaded.</summary>
    public static string KopiaExe => Variable("HH_KOPIA_EXE");
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
