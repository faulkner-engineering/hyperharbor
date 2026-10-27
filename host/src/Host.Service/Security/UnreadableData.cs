namespace HyperHarbor.Host.Service.Security;

/// <summary>What a portable host reports when it cannot open files in its data directory (see DataDirectoryAccess).</summary>
internal static class UnreadableData
{
    /// <summary>The portable host's exit code when it cannot open its data.</summary>
    public const int ExitCode = 3;

    /// <param name="unreadable">The paths the host could not open; at least one.</param>
    public static string Describe(string dataDirectory, IReadOnlyList<string> unreadable)
    {
        var first = Path.GetRelativePath(dataDirectory, unreadable[0]);
        var others = unreadable.Count - 1;
        var files = others == 0 ? first : $"{first} and {others} more";
        return $"HyperHarbor cannot open {files} in {dataDirectory}. An administrator account probably wrote "
            + "them while approving a setup prompt. Run Start-HyperHarbor.ps1 again and approve its prompt, or "
            + "choose Set up console access in the HyperHarbor Host window, to give this account access again.";
    }
}
