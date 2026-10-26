using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed partial class PassphraseGeneratorTests
{
    private static readonly string Account = GuestAdminAccountName.For("owner");
    private static readonly string FullName = GuestAdminAccountName.FullName("owner");

    [Fact]
    public void WordList_IsLargeUniqueAndEasyToType()
    {
        var words = PassphraseGenerator.Words;

        Assert.True(words.Count >= 1024, $"Only {words.Count} words.");
        Assert.Equal(words.Count, words.Distinct(StringComparer.Ordinal).Count());
        Assert.All(words, word => Assert.Matches("^[a-z]{4,8}$", word));
    }

    [Fact]
    public void Passphrase_IsThreeCapitalizedWordsAndADigit()
    {
        for (var i = 0; i < 200; i++)
        {
            var passphrase = PassphraseGenerator.Generate(Account, FullName);

            Assert.Matches(Shape(), passphrase);
            var parts = passphrase.Split('-');
            Assert.All(parts[..3], word => Assert.Contains(word.ToLowerInvariant(), PassphraseGenerator.Words));
        }
    }

    [Fact]
    public void Passphrase_MeetsTheDefaultWindowsComplexityPolicy()
    {
        for (var i = 0; i < 500; i++)
        {
            var passphrase = PassphraseGenerator.Generate(Account, FullName);

            Assert.True(PassphraseGenerator.MeetsWindowsComplexity(passphrase, Account, FullName), passphrase);
        }
    }

    /// <summary>
    /// Windows refuses a password that contains a part of the account's full name, and "for" and "owner" are parts of
    /// "HyperHarbor administrator for owner"; words like Forest or Comfort must never be drawn.
    /// </summary>
    [Fact]
    public void Passphrase_LeavesOutWordsContainingPartsOfTheFullName()
    {
        Assert.Contains(PassphraseGenerator.Words, word => word.Contains("for", StringComparison.Ordinal));
        Assert.False(PassphraseGenerator.MeetsWindowsComplexity("Forest-River-Orbit-7", Account, FullName));

        for (var i = 0; i < 500; i++)
        {
            var passphrase = PassphraseGenerator.Generate(Account, FullName);

            Assert.DoesNotContain("for", passphrase, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("owner", passphrase, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Complexity_NeedsThreeCharacterClasses()
    {
        Assert.False(PassphraseGenerator.MeetsWindowsComplexity("maple-river", Account, FullName));
        Assert.True(PassphraseGenerator.MeetsWindowsComplexity("Maple-River", Account, FullName));
        Assert.False(PassphraseGenerator.MeetsWindowsComplexity("Maple-hha-owner-7", Account, FullName));
    }

    [Fact]
    public void Passphrases_Differ()
    {
        var passphrases = Enumerable.Range(0, 100).Select(_ => PassphraseGenerator.Generate(Account, FullName)).ToHashSet();

        Assert.Equal(100, passphrases.Count);
    }

    [GeneratedRegex("^([A-Z][a-z]{3,7}-){3}[0-9]$")]
    private static partial Regex Shape();
}
