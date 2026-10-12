using HyperHarbor.Host.Core.Backup;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class RecoveryKeyServiceTests
{
    private sealed class MemorySecrets : IBackupSecretStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Get(string reference) => Values.GetValueOrDefault(reference);

        public void Set(string reference, string secret) => Values[reference] = secret;

        public void Remove(string reference) => Values.Remove(reference);
    }

    private readonly MemorySecrets _secrets = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

    private RecoveryKeyService Create() => new(_secrets, _time);

    private static string LastGroups(string key) => string.Join('-', key.Split('-')[^RecoveryKeyService.ConfirmGroups..]);

    [Fact]
    public void Begin_StoresNothing_UntilTheKeyIsConfirmed()
    {
        var offer = Create().Begin("repo1");

        Assert.Empty(_secrets.Values);
        Assert.Equal(13, offer.RecoveryKey.Split('-').Length);
        Assert.All(offer.RecoveryKey.Split('-'), group => Assert.Equal(4, group.Length));
    }

    [Fact]
    public void Confirm_WithTheLastGroups_StoresThePassphrase_AndReturnsTheRepository()
    {
        var service = Create();
        var offer = service.Begin("repo1");

        var repo = service.Confirm(offer.SetupId, LastGroups(offer.RecoveryKey).ToLowerInvariant());

        Assert.Equal("repo1", repo);
        Assert.Equal(offer.RecoveryKey, _secrets.Get(RecoveryKeyService.PassphraseReference("repo1")));
    }

    [Fact]
    public void Confirm_WithAWrongEntry_StoresNothing_AndKeepsTheOfferOpen()
    {
        var service = Create();
        var offer = service.Begin("repo1");

        Assert.Throws<RecoveryKeyException>(() => service.Confirm(offer.SetupId, "AAAA-AAAA-AAAA-AAAA"));
        Assert.Empty(_secrets.Values);

        service.Confirm(offer.SetupId, LastGroups(offer.RecoveryKey));
        Assert.NotEmpty(_secrets.Values);
    }

    [Fact]
    public void Confirm_CannotBeUsedTwice_SoTheKeyIsNeverOfferedAgain()
    {
        var service = Create();
        var offer = service.Begin("repo1");
        service.Confirm(offer.SetupId, LastGroups(offer.RecoveryKey));

        Assert.Throws<RecoveryKeyException>(() => service.Confirm(offer.SetupId, LastGroups(offer.RecoveryKey)));
    }

    [Fact]
    public void Confirm_AfterTheOfferExpires_Fails_AndStoresNothing()
    {
        var service = Create();
        var offer = service.Begin("repo1");
        _time.Advance(RecoveryKeyService.OfferLifetime + TimeSpan.FromSeconds(1));

        Assert.Throws<RecoveryKeyException>(() => service.Confirm(offer.SetupId, LastGroups(offer.RecoveryKey)));
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public void Cancel_DropsTheOffer()
    {
        var service = Create();
        var offer = service.Begin("repo1");
        service.Cancel(offer.SetupId);

        Assert.Throws<RecoveryKeyException>(() => service.Confirm(offer.SetupId, LastGroups(offer.RecoveryKey)));
    }

    [Fact]
    public void Keys_AreUnique_AndTheOfferHidesTheKeyInToString()
    {
        var service = Create();
        var a = service.Begin("repo1");
        var b = service.Begin("repo1");

        Assert.NotEqual(a.RecoveryKey, b.RecoveryKey);
        Assert.DoesNotContain(a.RecoveryKey, a.ToString());
    }
}
