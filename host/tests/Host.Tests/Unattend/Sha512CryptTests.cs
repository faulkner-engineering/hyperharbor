using HyperHarbor.Host.Core.Unattend;

namespace HyperHarbor.Host.Tests.Unattend;

public sealed class Sha512CryptTests
{
    /// <summary>Vectors from Drepper's specification and from openssl passwd -6.</summary>
    [Theory]
    [InlineData("Hello world!", "$6$saltstring", "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    [InlineData("Hello world!", "$6$saltstringsaltstring", "$6$saltstringsaltst$e.3mR68CqZEpesEX1HlFZT6sEanSOjM/b5UoDyDo00a8syek2cJldMjrbtKP86.FJvzluVR7nc3DNzelAwTxj.")]
    [InlineData("Hello world!", "$6$rounds=10000$saltstringsaltstring", "$6$rounds=10000$saltstringsaltst$OW1/O6BYHV6BcXZu8QVeXbDWra3Oeqh0sbHbbMCVNSnCM/UrjmM0Dp8vOuZeHBy/YTBmSK6H9qs/y3RnOaw5v.")]
    public void MatchesReferenceHashes(string password, string salt, string expected)
    {
        Assert.Equal(expected, Sha512Crypt.Hash(password, salt));
    }

    [Fact]
    public void PasswordsLongerThanTheDigest_MatchOpenSsl()
    {
        Assert.Equal("$6$longpw$BBqNtCUz..I.3nGiwIx777ynHeQVnuzgG5wYwl/7Jy2BI/5owlsOm9ZXEaAhnfcEwdfci.hwO60F0a.2eyy1o/", Sha512Crypt.Hash(new string('a', 100), "longpw"));
    }

    [Fact]
    public void RandomSalt_IsSixteenCharactersAndDiffersEachTime()
    {
        var first = Sha512Crypt.Hash("One-Time-Password1!");
        var second = Sha512Crypt.Hash("One-Time-Password1!");

        Assert.NotEqual(first, second);
        Assert.Matches(@"^\$6\$[./0-9A-Za-z]{16}\$[./0-9A-Za-z]{86}$", first);
        Assert.DoesNotContain("One-Time-Password1!", first, StringComparison.Ordinal);
    }
}
