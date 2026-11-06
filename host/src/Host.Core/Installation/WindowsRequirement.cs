namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// The host needs Windows 11 (from 0.3.0). The installer refuses on older Windows, and so does the host itself, which
/// includes the self-test a new version passes before it is installed: a 0.2.x host on Windows 10 therefore stays on
/// 0.2.x instead of updating to a version that cannot run there (the self-test failure keeps the old version).
/// </summary>
public static class WindowsRequirement
{
    /// <summary>The first Windows 11 build; Windows Server 2025 (26100) passes, Windows Server 2022 (20348) does not.</summary>
    public const int MinimumBuild = 22000;

    /// <summary>The host's exit code on older Windows.</summary>
    public const int ExitCode = 4;

    /// <summary>Why <paramref name="os"/> cannot run the host, or null when it can.</summary>
    /// <param name="os">The Windows version (Environment.OSVersion.Version reports the real one on .NET 5 and later).</param>
    public static string? Problem(Version os) =>
        os.Major > 10 || (os.Major == 10 && os.Build >= MinimumBuild)
            ? null
            : $"HyperHarbor Host needs Windows 11. This PC runs Windows build {os.Build}; HyperHarbor 0.2.6 is the last version for Windows 10.";

    /// <summary><see cref="Problem(Version)"/> for this PC.</summary>
    public static string? ProblemOnThisPc() => Problem(Environment.OSVersion.Version);
}
