using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>Case-insensitive matching with * (any run of characters) and ? (one character), as startup entries, services, and programs are named.</summary>
public static class Wildcard
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string text) =>
        Cache.GetOrAdd(pattern, static value => new Regex(
            "^" + Regex.Escape(value).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
            TimeSpan.FromSeconds(1))).IsMatch(text);

    public static bool IsMatchAny(IEnumerable<string> patterns, string text) => patterns.Any(pattern => IsMatch(pattern, text));
}
