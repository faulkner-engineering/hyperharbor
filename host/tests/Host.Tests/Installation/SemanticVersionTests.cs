using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0, null)]
    [InlineData("1.20.300", 1, 20, 300, null)]
    [InlineData("2.0.0-beta.1", 2, 0, 0, "beta.1")]
    [InlineData("0.1.0+3f2a9c1", 0, 1, 0, null)]
    [InlineData("1.0.0-rc.2+build.5", 1, 0, 0, "rc.2")]
    public void Parse_ReadsCoreAndPrerelease_AndIgnoresBuildMetadata(string text, int major, int minor, int patch, string? prerelease)
    {
        var version = SemanticVersion.Parse(text);

        Assert.Equal(new SemanticVersion(major, minor, patch, prerelease), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta/1")]
    [InlineData("..\\1.0.0")]
    public void TryParse_RejectsAnythingButSemver(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void Ordering_FollowsSemver()
    {
        string[] ascending = ["0.9.9", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];

        var versions = ascending.Select(SemanticVersion.Parse).ToList();

        Assert.Equal(versions, versions.OrderBy(version => version).ToList());
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.True(versions[i - 1] < versions[i], $"{versions[i - 1]} < {versions[i]}");
        }
    }

    [Fact]
    public void ToString_IsTheFolderName()
    {
        Assert.Equal("1.2.3-rc.1", SemanticVersion.Parse("1.2.3-rc.1+abc").ToString());
    }
}
