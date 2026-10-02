using HyperHarbor.Host.Core.Identity;

namespace HyperHarbor.Host.Tests.Discovery;

public sealed class HostIdentityStoreTests : IDisposable
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
    public void GetOrCreateHostId_CreatesAndPersistsId()
    {
        var first = new HostIdentityStore(_directory).GetOrCreateHostId();
        var second = new HostIdentityStore(_directory).GetOrCreateHostId();

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(_directory, "host-identity.json")));
    }

    [Fact]
    public void GetOrCreateHostId_InvalidFile_ThrowsInsteadOfReplacingIdentity()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "host-identity.json"), """{"HostId":"00000000-0000-0000-0000-000000000000"}""");

        Assert.Throws<InvalidDataException>(() => new HostIdentityStore(_directory).GetOrCreateHostId());
    }
}
