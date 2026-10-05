using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class DataFormatTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Missing_IsFormatOne_AndIsRecordedAsCurrent()
    {
        var found = DataFormat.EnsureCurrent(_directory);

        Assert.Equal(1, found);
        Assert.Equal(DataFormat.Current, DataFormat.Read(_directory));
    }

    [Fact]
    public void Newer_RefusesToStart_AndLeavesTheMarkerAlone()
    {
        File.WriteAllText(Path.Combine(_directory, DataFormat.FileName), """{ "format": 99 }""");

        var error = Assert.Throws<DataFormatTooNewException>(() => DataFormat.EnsureCurrent(_directory));

        Assert.Equal(99, error.Found);
        Assert.Equal(DataFormat.Current, error.Supported);
        Assert.Equal(99, DataFormat.Read(_directory));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": 0 }""")]
    [InlineData("{}")]
    public void Unreadable_IsAnError(string contents)
    {
        File.WriteAllText(Path.Combine(_directory, DataFormat.FileName), contents);

        Assert.Throws<InvalidDataException>(() => DataFormat.EnsureCurrent(_directory));
    }

    [Fact]
    public void Older_RunsEachMigrationInOrder_AndRecordsEachStep()
    {
        File.WriteAllText(Path.Combine(_directory, DataFormat.FileName), """{ "format": 1 }""");
        var steps = new List<string>();
        var migrations = new Dictionary<int, Action<string>>
        {
            [1] = directory => steps.Add($"1->2 at format {DataFormat.Read(directory)}"),
            [2] = directory => steps.Add($"2->3 at format {DataFormat.Read(directory)}"),
        };

        var found = DataFormat.EnsureCurrent(_directory, current: 3, migrations);

        Assert.Equal(1, found);
        Assert.Equal(["1->2 at format 1", "2->3 at format 2"], steps);
        Assert.Equal(3, DataFormat.Read(_directory));
    }

    [Fact]
    public void AFailedMigration_LeavesTheLastCompletedFormat()
    {
        var migrations = new Dictionary<int, Action<string>>
        {
            [1] = _ => { },
            [2] = _ => throw new IOException("disk full"),
        };

        Assert.Throws<IOException>(() => DataFormat.EnsureCurrent(_directory, current: 3, migrations));

        Assert.Equal(2, DataFormat.Read(_directory));
    }

    [Fact]
    public void AMissingMigration_IsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => DataFormat.EnsureCurrent(_directory, current: 2, new Dictionary<int, Action<string>>()));
    }
}
