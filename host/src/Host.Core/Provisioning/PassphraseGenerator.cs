using System.Security.Cryptography;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// Generates passphrases for administrator windows: three words from a bundled list of common English words, each
/// capitalized, then a digit, separated by hyphens (for example "Maple-River-Orbit-7"). A person can read one
/// on one screen and type it on another. Upper and lower case letters, a digit, and a hyphen meet the default Windows
/// complexity policy, which also refuses a password that contains the account name or a part of its full name;
/// <see cref="Generate"/> leaves out words that would.
/// </summary>
public static class PassphraseGenerator
{
    public const int WordCount = 3;

    private const string ResourceName = "HyperHarbor.Provisioning.PassphraseWords.txt";

    /// <summary>The characters Windows splits a full name on when it checks a password against it.</summary>
    private static readonly char[] NameDelimiters = [',', '.', '-', '_', ' ', '#', '\t'];

    private static readonly Lazy<string[]> LazyWords = new(LoadWords);

    /// <summary>The bundled list, lowercase.</summary>
    public static IReadOnlyList<string> Words => LazyWords.Value;

    /// <param name="accountName">The account the passphrase is for; it must not appear in the passphrase.</param>
    /// <param name="fullName">The account's full name; none of its parts of three or more characters may appear.</param>
    public static string Generate(string accountName, string? fullName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        var forbidden = ForbiddenParts(accountName, fullName);
        var allowed = Words.Where(word => !forbidden.Any(part => word.Contains(part, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (allowed.Length < 256)
        {
            throw new InvalidOperationException("The account name leaves too few passphrase words.");
        }

        var parts = RandomNumberGenerator.GetItems<string>(allowed, WordCount)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..])
            .Append(RandomNumberGenerator.GetInt32(0, 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join('-', parts);
    }

    /// <summary>
    /// Whether <paramref name="password"/> meets the default Windows complexity policy for the account: three of the
    /// four character classes (upper case, lower case, digit, other), no account name, and no full-name part of three
    /// or more characters, compared without case.
    /// </summary>
    public static bool MeetsWindowsComplexity(string password, string accountName, string? fullName = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        var classes = new[]
        {
            password.Any(char.IsUpper),
            password.Any(char.IsLower),
            password.Any(char.IsDigit),
            password.Any(c => !char.IsLetterOrDigit(c)),
        }.Count(present => present);
        return classes >= 3 && !ForbiddenParts(accountName, fullName).Any(part => password.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ForbiddenParts(string accountName, string? fullName)
    {
        var parts = new List<string>();
        if (accountName.Length >= 3)
        {
            parts.Add(accountName);
        }

        parts.AddRange((fullName ?? string.Empty).Split(NameDelimiters, StringSplitOptions.RemoveEmptyEntries).Where(part => part.Length >= 3));
        return parts;
    }

    private static string[] LoadWords()
    {
        using var stream = typeof(PassphraseGenerator).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
