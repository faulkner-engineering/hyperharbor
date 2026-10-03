using System.Diagnostics;
using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class IsoLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _library;
    private readonly string _outside;
    private readonly IsoLibrary _isos;

    public IsoLibraryTests()
    {
        _library = Path.Combine(_root, "isos");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(_library, "Windows"));
        Directory.CreateDirectory(_outside);
        File.WriteAllBytes(Path.Combine(_library, "ubuntu.iso"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_library, "Windows", "Win11.ISO"), new byte[20]);
        File.WriteAllText(Path.Combine(_library, "readme.txt"), "not an image");
        File.WriteAllBytes(Path.Combine(_outside, "secret.iso"), new byte[5]);
        _isos = new IsoLibrary(_library);
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
    public void List_ReturnsIsoFilesInSubfolders_WithRelativeNames()
    {
        var images = _isos.List();

        Assert.Equal(["ubuntu.iso", @"Windows\Win11.ISO"], images.Select(image => image.Name));
        Assert.Equal(20, images[1].SizeBytes);
    }

    [Fact]
    public void List_CreatesAMissingFolder()
    {
        var library = new IsoLibrary(Path.Combine(_root, "new"));

        Assert.Empty(library.List());
        Assert.True(Directory.Exists(library.Folder));
    }

    [Theory]
    [InlineData("ubuntu.iso")]
    [InlineData(@"Windows\Win11.ISO")]
    [InlineData("Windows/Win11.ISO")]
    public void Resolve_ReturnsTheFullPath(string name)
    {
        var path = _isos.Resolve(name);

        Assert.StartsWith(_library + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(@"..\outside\secret.iso")]
    [InlineData(@"Windows\..\..\outside\secret.iso")]
    [InlineData("../outside/secret.iso")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts.iso")]
    [InlineData(@"\\server\share\x.iso")]
    [InlineData(@"\outside\secret.iso")]
    [InlineData("C:secret.iso")]
    [InlineData("readme.txt")]
    [InlineData("missing.iso")]
    [InlineData("")]
    public void Resolve_RejectsNamesOutsideTheLibrary(string name)
    {
        var ex = Assert.Throws<LifecycleValidationException>(() => _isos.Resolve(name));

        Assert.Equal("isoName", Assert.Single(ex.Errors).Field);
    }

    [Fact]
    public void Resolve_RejectsAJunctionThatLeavesTheLibrary()
    {
        CreateJunction(Path.Combine(_library, "linked"), _outside);

        Assert.Throws<LifecycleValidationException>(() => _isos.Resolve(@"linked\secret.iso"));
        Assert.DoesNotContain(_isos.List(), image => image.Name.Contains("secret", StringComparison.Ordinal));
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
}
