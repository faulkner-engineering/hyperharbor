using System.Diagnostics;
using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class IsoLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _library;
    private readonly string _outside;
    private readonly FakeHyperVStorage _storage = new();
    private readonly IsoLibrary _isos;
    private readonly IsoLibraryService _service;

    public IsoLibraryTests()
    {
        _library = Path.Combine(_root, "isos");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(_library, "Old"));
        Directory.CreateDirectory(_outside);
        File.WriteAllBytes(Path.Combine(_library, "ubuntu.iso"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_library, "Win11.ISO"), new byte[20]);
        File.WriteAllBytes(Path.Combine(_library, "Old", "nested.iso"), new byte[1]);
        File.WriteAllText(Path.Combine(_library, "readme.txt"), "not an image");
        File.WriteAllBytes(Path.Combine(_outside, "secret.iso"), new byte[5]);
        _isos = new IsoLibrary(_library);
        _service = new IsoLibraryService(_isos, _storage);
    }

    public void Dispose()
    {
        // Remove the junction first so deleting the tree does not follow it.
        var junction = Path.Combine(_library, "linked");
        if (Directory.Exists(junction))
        {
            Directory.Delete(junction);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void List_ReturnsIsoFilesInTheFolderItself()
    {
        var images = _isos.List();

        Assert.Equal(["ubuntu.iso", "Win11.ISO"], images.Select(image => image.Name));
        Assert.Equal(20, images[1].SizeBytes);
    }

    [Fact]
    public void List_CreatesAMissingFolder()
    {
        var library = new IsoLibrary(Path.Combine(_root, "new"));

        Assert.Empty(library.List());
        Assert.True(Directory.Exists(library.Folder));
    }

    [Fact]
    public void Folder_IsReadOnEveryCall()
    {
        var folder = _library;
        var library = new IsoLibrary(() => folder);
        Assert.Equal(2, library.List().Count);

        folder = Path.Combine(_root, "moved");

        Assert.Empty(library.List());
        Assert.Equal(Path.Combine(_root, "moved"), library.Folder);
    }

    [Fact]
    public void Resolve_ReturnsTheFullPath()
    {
        Assert.Equal(Path.Combine(_library, "ubuntu.iso"), _isos.Resolve("ubuntu.iso"));
    }

    [Theory]
    [InlineData(@"..\outside\secret.iso")]
    [InlineData("../outside/secret.iso")]
    [InlineData(@"Old\nested.iso")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts.iso")]
    [InlineData(@"\\server\share\x.iso")]
    [InlineData("C:secret.iso")]
    [InlineData("readme.txt")]
    [InlineData("missing.iso")]
    [InlineData(".hidden.iso")]
    [InlineData("CON.iso")]
    [InlineData("")]
    public void Resolve_RejectsNamesOutsideTheLibrary(string name)
    {
        var ex = Assert.Throws<LifecycleValidationException>(() => _isos.Resolve(name));

        Assert.Equal("isoName", Assert.Single(ex.Errors).Field);
    }

    [Fact]
    public void Resolve_RejectsAJunctionToAnImageOutsideTheLibrary()
    {
        CreateJunction(Path.Combine(_library, "linked"), _outside);

        Assert.Throws<LifecycleValidationException>(() => _isos.Resolve(@"linked\secret.iso"));
        Assert.DoesNotContain(_isos.List(), image => image.Name.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_WritesTheImage_AndLeavesNoPartialFile()
    {
        var content = new byte[3 * 1024 * 1024 + 7];
        Random.Shared.NextBytes(content);

        var image = await _isos.SaveAsync("debian.iso", new MemoryStream(content), content.Length, CancellationToken.None);

        Assert.Equal("debian.iso", image.Name);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_library, "debian.iso")));
        Assert.Empty(Directory.EnumerateFiles(_library, "*.partial"));
    }

    [Fact]
    public async Task Save_ThatEndsEarly_AddsNothing()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            _isos.SaveAsync("debian.iso", new MemoryStream(new byte[100]), 200, CancellationToken.None));

        Assert.Contains("100 of 200", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_library, "debian.iso")));
        Assert.Empty(Directory.EnumerateFiles(_library, "*.partial"));
    }

    [Fact]
    public async Task Save_ThatIsCancelled_AddsNothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _isos.SaveAsync("debian.iso", new MemoryStream(new byte[100]), 100, cancel.Token));

        Assert.False(File.Exists(Path.Combine(_library, "debian.iso")));
        Assert.Empty(Directory.EnumerateFiles(_library, "*.partial"));
    }

    [Theory]
    [InlineData("ubuntu.iso")]
    [InlineData("UBUNTU.ISO")]
    public async Task Save_OverAnExistingName_IsAConflict(string name)
    {
        await Assert.ThrowsAsync<LifecycleConflictException>(() =>
            _isos.SaveAsync(name, new MemoryStream(new byte[1]), 1, CancellationToken.None));

        Assert.Equal(10, new FileInfo(Path.Combine(_library, "ubuntu.iso")).Length);
    }

    [Theory]
    [InlineData(@"..\escape.iso")]
    [InlineData("image.img")]
    [InlineData("bad|name.iso")]
    public async Task Save_WithAnInvalidName_IsRefused(string name)
    {
        await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _isos.SaveAsync(name, new MemoryStream(new byte[1]), 1, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "escape.iso")));
    }

    [Fact]
    public async Task Save_LargerThanTheFreeSpace_IsRefusedBeforeReading()
    {
        var content = new ThrowingStream();

        await Assert.ThrowsAsync<InsufficientStorageException>(() =>
            _isos.SaveAsync("huge.iso", content, long.MaxValue / 2, CancellationToken.None));
    }

    [Fact]
    public void List_DeletesOldPartialUploads()
    {
        var stale = Path.Combine(_library, ".old.iso.0123.partial");
        var fresh = Path.Combine(_library, ".new.iso.4567.partial");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        _isos.List();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Rename_MovesTheFile_IncludingACaseOnlyChange()
    {
        var renamed = await _service.RenameAsync("ubuntu.iso", "Ubuntu 24.04.iso", CancellationToken.None);
        Assert.Equal("Ubuntu 24.04.iso", renamed.Name);

        var cased = await _service.RenameAsync("Ubuntu 24.04.iso", "UBUNTU 24.04.iso", CancellationToken.None);
        Assert.Equal("UBUNTU 24.04.iso", cased.Name);
        Assert.Equal(["UBUNTU 24.04.iso", "Win11.ISO"], _isos.List().Select(image => image.Name));
    }

    [Fact]
    public async Task Rename_OntoAnotherImage_IsAConflict()
    {
        await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.RenameAsync("ubuntu.iso", "win11.iso", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesTheFile_AndUnknownNamesAreNotFound()
    {
        await _service.DeleteAsync("ubuntu.iso", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_library, "ubuntu.iso")));
        await Assert.ThrowsAsync<IsoNotFoundException>(() => _service.DeleteAsync("ubuntu.iso", CancellationToken.None));
    }

    [Fact]
    public async Task ImagesAttachedToAVm_AreListedWithTheVm_AndCannotBeDeletedOrRenamed()
    {
        _storage.Images.Add(new DiskAttachment(Guid.NewGuid(), "Win11 Dev", Path.Combine(_library, "win11.iso"), InCheckpoint: false));
        _storage.Images.Add(new DiskAttachment(Guid.NewGuid(), "Lab", Path.Combine(_library, "Win11.ISO"), InCheckpoint: true));
        _storage.Images.Add(new DiskAttachment(Guid.NewGuid(), "Elsewhere", Path.Combine(_outside, "ubuntu.iso"), InCheckpoint: false));

        var images = await _service.ListAsync(CancellationToken.None);

        Assert.Equal(["Lab", "Win11 Dev"], images.Single(image => image.Name == "Win11.ISO").UsedBy);
        Assert.Empty(images.Single(image => image.Name == "ubuntu.iso").UsedBy);
        var ex = await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.DeleteAsync("Win11.ISO", CancellationToken.None));
        Assert.Contains("Win11 Dev", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.RenameAsync("Win11.ISO", "w.iso", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_library, "Win11.ISO")));
    }

    [Fact]
    public void HostSettings_RememberTheFolder()
    {
        var store = new HostSettingsStore(_root);
        Assert.Null(store.IsoFolder);

        store.SetIsoFolder(@"D:\ISOs");

        Assert.Equal(@"D:\ISOs", new HostSettingsStore(_root).IsoFolder);
    }

    private static void CreateJunction(string link, string target)
    {
        // Junctions need no special privilege, unlike symbolic links.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>Fails the test if anything reads it.</summary>
    private sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The body was read.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The body was read.");
    }
}
