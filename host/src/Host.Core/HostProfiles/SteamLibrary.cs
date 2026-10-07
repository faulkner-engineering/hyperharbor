using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>
/// Finds installed Steam games: libraryfolders.vdf in the Steam folder lists the library folders, and each library's
/// steamapps folder holds one appmanifest_&lt;appId&gt;.acf per installed game. Read-only.
/// </summary>
public sealed partial class SteamLibrary(Func<string?> steamFolder) : ISteamLibrary
{
    /// <summary>Steam's StateFlags bit for a fully installed game.</summary>
    private const int FullyInstalled = 4;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathLine();

    [GeneratedRegex("\"name\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameLine();

    [GeneratedRegex("\"StateFlags\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StateLine();

    [GeneratedRegex(@"^appmanifest_(\d+)\.acf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ManifestFile();

    /// <summary>Steam's install folder from the registry, or its usual place.</summary>
    public static string? FolderFromRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (key?.GetValue("InstallPath") is string path && Directory.Exists(path))
        {
            return path;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public IReadOnlyList<SteamGame> InstalledGames()
    {
        if (steamFolder() is not { } steam)
        {
            return [];
        }

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                libraries.AddRange(PathLine().Matches(File.ReadAllText(vdf)).Select(match => match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal)));
            }
        }
        catch (IOException)
        {
            // An unreadable list leaves Steam's own folder, which is where most games are.
        }

        var games = new Dictionary<long, SteamGame>();
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                if (ManifestFile().Match(Path.GetFileName(file)) is not { Success: true } match
                    || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                if (StateLine().Match(text) is { Success: true } state
                    && int.TryParse(state.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
                    && (flags & FullyInstalled) == 0)
                {
                    continue;
                }

                games[appId] = new SteamGame(appId, NameLine().Match(text) is { Success: true } name ? name.Groups[1].Value : appId.ToString(CultureInfo.InvariantCulture));
            }
        }

        return games.Values.OrderBy(game => game.AppId).ToList();
    }
}
