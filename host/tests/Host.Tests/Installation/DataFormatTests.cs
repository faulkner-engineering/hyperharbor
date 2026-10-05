using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Unattend;

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

    [Fact]
    public void FormatOneInstalls_ReadAfterTheMigration_WithoutASetupProfile()
    {
        File.WriteAllText(Path.Combine(_directory, DataFormat.FileName), """{ "format": 1 }""");
        File.WriteAllText(Path.Combine(_directory, "unattended-installs.json"), """
            [{ "vmId": "4bcff7d6-c84d-4f34-88c7-7ea845092177", "userId": "e17ed7d6-09a0-436d-badc-8b3bdeb3c9dc",
               "profileId": "windows-workstation", "os": "windows", "installDesktop": false, "seedPath": "C:\\VMs\\seed.iso",
               "state": "ready", "step": "Ready", "startedAt": "2026-10-04T12:00:00Z", "updatedAt": "2026-10-04T12:30:00Z", "attempts": 0 }]
            """);

        DataFormat.EnsureCurrent(_directory);

        Assert.Equal(2, DataFormat.Read(_directory));
        var install = Assert.Single(new UnattendedInstallStore(_directory).List());
        Assert.Equal(UnattendedInstallState.Ready, install.State);
        Assert.Null(install.SetupProfile);
        Assert.False(install.AccountConfigured);
    }
}
