using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Tests.Installation;

/// <summary>Real junctions in a temporary folder; creating a junction needs no elevation.</summary>
public sealed class InstallLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly InstallLayout _layout;

    public InstallLayoutTests()
    {
        Directory.CreateDirectory(_root);
        _layout = new InstallLayout(Path.Combine(_root, "HyperHarbor"));
    }

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(Junction.IsJunction).ToList())
        {
            Junction.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Junction_PointsAtItsTarget_AndDeletingItKeepsTheTarget()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "file.txt"), "kept");
        var link = Path.Combine(_root, "link");

        Junction.Create(link, target);

        Assert.True(Junction.IsJunction(link));
        Assert.Equal(target, Junction.GetTarget(link), ignoreCase: true);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(link, "file.txt")));

        Junction.Delete(link);

        Assert.False(Directory.Exists(link));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "file.txt")));
    }

    [Fact]
    public void Junction_RefusesAnExistingPath_AndAMissingTarget()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        Assert.Throws<IOException>(() => Junction.Create(folder, target));
        Assert.Throws<DirectoryNotFoundException>(() => Junction.Create(Path.Combine(_root, "link"), Path.Combine(_root, "missing")));
        Assert.False(Directory.Exists(Path.Combine(_root, "link")));
        Assert.Null(Junction.GetTarget(folder));
        Assert.Throws<IOException>(() => Junction.Delete(folder));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Junction_Repoint_ReplacesTheTarget_AndClearsALeftoverNextJunction()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        var link = Path.Combine(_root, "current");
        Junction.Repoint(link, first);
        Junction.Create(link + ".next", first);

        Junction.Repoint(link, second);

        Assert.Equal(second, Junction.GetTarget(link), ignoreCase: true);
        Assert.False(Directory.Exists(link + ".next"));
        Assert.True(Directory.Exists(first));
    }

    [Fact]
    public void Junction_Repoint_RefusesToReplaceARealFolder()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Directory.CreateDirectory(Path.Combine(_root, "current")).FullName;
        File.WriteAllText(Path.Combine(link, "data.txt"), "precious");

        Assert.Throws<IOException>(() => Junction.Repoint(link, target));

        Assert.Equal("precious", File.ReadAllText(Path.Combine(link, "data.txt")));
        Assert.False(Directory.Exists(link + ".next"));
    }

    [Fact]
    public void StageAndActivate_PointCurrentAtTheVersionFolder()
    {
        var version = SemanticVersion.Parse("1.2.0");
        var source = WriteExecutable("one");

        var staged = _layout.Stage(source, version);
        _layout.Activate(version);

        Assert.Equal(Path.Combine(_layout.Root, "versions", "1.2.0", InstallLayout.ExecutableName), staged);
        Assert.Equal(version, _layout.CurrentVersion);
        Assert.Equal("one", File.ReadAllText(_layout.CurrentExecutable));
        Assert.False(File.Exists(staged + ".partial"));
    }

    [Fact]
    public void Stage_LeavesAnIdenticalRunningExecutableAlone()
    {
        var version = SemanticVersion.Parse("1.0.0");
        var staged = _layout.Stage(WriteExecutable("same"), version);

        // An open handle without delete sharing stands in for the running executable.
        using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(staged, _layout.Stage(WriteExecutable("same"), version));
        }
    }

    [Fact]
    public async Task Stage_WaitsForALockedExecutableToBeReleased()
    {
        var version = SemanticVersion.Parse("1.0.0");
        var staged = _layout.Stage(WriteExecutable("old"), version);
        var locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => locked.Dispose(), TaskScheduler.Default);

        _layout.Stage(WriteExecutable("new"), version, unlockTimeout: TimeSpan.FromSeconds(10));

        await release;
        Assert.Equal("new", File.ReadAllText(staged));
    }

    [Fact]
    public void Stage_GivesUpWhenTheExecutableStaysLocked()
    {
        var version = SemanticVersion.Parse("1.0.0");
        var staged = _layout.Stage(WriteExecutable("old"), version);

        using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => _layout.Stage(WriteExecutable("new"), version, unlockTimeout: TimeSpan.FromSeconds(1)));
            Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected failure: {failure}");
        }

        Assert.Equal("old", File.ReadAllText(staged));
    }

    [Fact]
    public void InstallHelper_ReplacesARunningHelper_ByRenamingItAside()
    {
        _layout.InstallHelper(WriteExecutable("helper 1"));

        // A running executable's image allows rename and delete sharing, so it can be moved aside but not overwritten.
        using (new FileStream(_layout.HelperExecutable, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            _layout.InstallHelper(WriteExecutable("helper 2"));
        }

        Assert.Equal("helper 2", File.ReadAllText(_layout.HelperExecutable));

        _layout.InstallHelper(WriteExecutable("helper 3"));
        Assert.Equal("helper 3", File.ReadAllText(_layout.HelperExecutable));
        Assert.False(File.Exists(_layout.HelperExecutable + ".partial"));
    }

    [Fact]
    public void Activate_RefusesAVersionThatIsNotStaged()
    {
        Assert.Throws<FileNotFoundException>(() => _layout.Activate(SemanticVersion.Parse("9.9.9")));
        Assert.Null(_layout.CurrentVersion);
    }

    [Fact]
    public void PruneExcept_KeepsTheNamedVersionsAndCurrent()
    {
        foreach (var text in new[] { "0.8.0", "0.9.0", "1.0.0", "1.1.0" })
        {
            _layout.Stage(WriteExecutable(text), SemanticVersion.Parse(text));
        }

        _layout.Activate(SemanticVersion.Parse("1.1.0"));
        Directory.CreateDirectory(Path.Combine(_layout.VersionsFolder, "not-a-version"));

        var deleted = _layout.PruneExcept(SemanticVersion.Parse("1.0.0"));

        Assert.Equal(["0.8.0", "0.9.0"], deleted.Select(version => version.ToString()));
        Assert.Equal(["1.0.0", "1.1.0"], _layout.InstalledVersions.Select(version => version.ToString()));
        Assert.True(Directory.Exists(Path.Combine(_layout.VersionsFolder, "not-a-version")));
    }

    [Fact]
    public void Contains_MatchesOnlyPathsInsideTheRoot()
    {
        Assert.True(_layout.Contains(_layout.CurrentExecutable));
        Assert.False(_layout.Contains(_layout.Root + "-other\\HyperHarbor.Host.exe"));
        Assert.False(_layout.Contains(Path.Combine(_root, "HyperHarbor.Host.exe")));
    }

    private string WriteExecutable(string contents)
    {
        var path = Path.Combine(_root, $"source-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, contents);
        return path;
    }
}
