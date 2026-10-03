using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Tests.Provisioning;

/// <summary>
/// Arguments reach the guest through a shell command line, so each must stay one literal word. In
/// POSIX single quotes nothing is special except the closing quote, which Quote writes as '\''.
/// </summary>
public class SshQuotingTests
{
    [Theory]
    [InlineData("hh-owner")]
    [InlineData("")]
    [InlineData("it's")]
    [InlineData("'")]
    [InlineData("''")]
    [InlineData("$(reboot)")]
    [InlineData("`reboot`")]
    [InlineData("a; rm -rf / #")]
    [InlineData("line\nbreak")]
    [InlineData("\\'\"")]
    [InlineData("${IFS}*?[]")]
    public void Quote_ProducesOneLiteralWord(string value)
    {
        var quoted = SshAccountManager.Quote(value);

        Assert.StartsWith("'", quoted, StringComparison.Ordinal);
        Assert.EndsWith("'", quoted, StringComparison.Ordinal);

        // Every quote inside the outer pair belongs to an '\'' sequence, so the shell never leaves
        // quoted mode except to emit a literal quote.
        var inner = quoted[1..^1];
        Assert.DoesNotContain("'", inner.Replace("'\\''", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(value, inner.Replace("'\\''", "'", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandLine_QuotesEveryArgument()
    {
        var line = SshAccountManager.CommandLine(asRoot: true, ["provision", "hh-owner", "$(id)"]);

        Assert.EndsWith(" hyperharbor 'provision' 'hh-owner' '$(id)'", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "SHA256:any", true)]
    [InlineData("SHA256:pinned", "SHA256:pinned", true)]
    [InlineData("SHA256:pinned", "SHA256:other", false)]
    [InlineData("SHA256:pinned", "sha256:pinned", false)]
    [InlineData("SHA256:pinned", "", false)]
    public void IsTrustedHostKey_TrustsOnFirstUse_ThenOnlyThePinnedKey(string? pinned, string presented, bool trusted)
    {
        Assert.Equal(trusted, SshAccountManager.IsTrustedHostKey(pinned, presented));
    }
}
