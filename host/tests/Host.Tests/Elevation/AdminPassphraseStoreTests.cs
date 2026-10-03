using System.Security.AccessControl;
using System.Security.Principal;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tests.Elevation;

public sealed class AdminPassphraseStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Set_PersistsOnlyTheHash_AndAnotherInstanceVerifiesIt()
    {
        var hash = AdminPassphrase.CreateHash("correct horse battery", ElevationServiceTests.TestIterations);
        new AdminPassphraseStore(_directory).Set(hash.Salt, hash.Hash, hash.Iterations);

        var reloaded = new AdminPassphraseStore(_directory);

        Assert.True(reloaded.IsConfigured);
        Assert.True(reloaded.Verify("correct horse battery"));
        Assert.False(reloaded.Verify("correct horse batterY"));
        Assert.False(reloaded.Verify(string.Empty));
        Assert.DoesNotContain("correct horse battery", File.ReadAllText(Path.Combine(_directory, AdminPassphraseStore.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_WithoutAPassphrase_IsFalse()
    {
        var store = new AdminPassphraseStore(_directory);

        Assert.False(store.IsConfigured);
        Assert.False(store.Verify("anything at all"));
    }

    [Theory]
    [InlineData(15, 32, 1000)]
    [InlineData(16, 31, 1000)]
    [InlineData(16, 32, 0)]
    public void Set_RejectsMalformedHashes(int saltBytes, int hashBytes, int iterations)
    {
        var store = new AdminPassphraseStore(_directory);

        Assert.Throws<ArgumentException>(() => store.Set(new byte[saltBytes], new byte[hashBytes], iterations));
        Assert.False(store.IsConfigured);
    }

    [Fact]
    public void File_HasTheRestrictedAcl()
    {
        var hash = AdminPassphrase.CreateHash("correct horse battery", ElevationServiceTests.TestIterations);
        new AdminPassphraseStore(_directory).Set(hash.Salt, hash.Hash, hash.Iterations);

        var security = new FileInfo(Path.Combine(_directory, AdminPassphraseStore.FileName)).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), allowed);
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("        ", false)]
    [InlineData("eight ch", true)]
    public void Validate_EnforcesLength(string passphrase, bool valid)
    {
        Assert.Equal(valid, AdminPassphrase.Validate(passphrase) is null);
    }

    [Fact]
    public void Validate_RejectsOverlongPassphrases()
    {
        Assert.NotNull(AdminPassphrase.Validate(new string('x', AdminPassphrase.MaximumLength + 1)));
    }

    [Fact]
    public void SetMessage_ToStringHidesTheHash()
    {
        var message = AdminPassphrase.CreateHash("correct horse battery", ElevationServiceTests.TestIterations);

        Assert.DoesNotContain(Convert.ToHexString(message.Hash)[..8], message.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hash =", message.ToString(), StringComparison.Ordinal);
    }
}
