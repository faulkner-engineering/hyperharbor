using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class ProfileImageStoreTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10];

    private readonly string _data = Directory.CreateTempSubdirectory("hh-images-").FullName;
    private readonly ProfileImageStore _store;

    public ProfileImageStoreTests() => _store = new ProfileImageStore(_data);

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public void Images_AreKeptPerUser_ByName()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        _store.Save(alice, "Dunes.jpg", Jpeg);
        _store.Save(alice, "aurora.jpg", Jpeg);

        Assert.Equal(["aurora.jpg", "Dunes.jpg"], _store.List(alice).Select(image => image.Name));
        Assert.Empty(_store.List(bob));
        Assert.NotNull(_store.PathOf(alice, "Dunes.jpg"));
        Assert.Null(_store.PathOf(bob, "Dunes.jpg"));
    }

    [Theory]
    [InlineData("..\\secrets.jpg")]
    [InlineData("../up.png")]
    [InlineData("a..b.png")]
    [InlineData("C:\\image.jpg")]
    [InlineData("image.exe")]
    [InlineData(".hidden.png")]
    public void NamesThatAreNotPlainImageFileNames_AreRefused(string name)
    {
        Assert.Throws<LifecycleValidationException>(() => _store.Save(Guid.NewGuid(), name, Jpeg));
        Assert.Null(_store.PathOf(Guid.NewGuid(), name));
    }

    [Fact]
    public void ContentThatIsNotAnImage_IsRefused_WhateverTheName()
    {
        var error = Assert.Throws<LifecycleValidationException>(() => _store.Save(Guid.NewGuid(), "photo.jpg", "MZ executable"u8.ToArray()));

        Assert.Equal("content", Assert.Single(error.Errors).Field);
    }

    [Fact]
    public void Delete_RemovesTheImage_AndAMissingOneIsNotFound()
    {
        var user = Guid.NewGuid();
        _store.Save(user, "dunes.jpg", Jpeg);

        _store.Delete(user, "dunes.jpg");

        Assert.Empty(_store.List(user));
        Assert.Throws<ProfileImageNotFoundException>(() => _store.Delete(user, "dunes.jpg"));
    }

    [Fact]
    public void TheGuestPath_IsInThePublicPicturesFolder()
    {
        Assert.Equal(@"C:\Users\Public\Pictures\HyperHarbor\dunes.jpg", ProfileImageStore.GuestPath("dunes.jpg"));
    }
}
