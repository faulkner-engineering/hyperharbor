using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Audit;

namespace HyperHarbor.Host.Tests.Audit;

public sealed class FileAuditLogTests : IDisposable
{
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Write_AppendsOneJsonLinePerEntry()
    {
        var log = new FileAuditLog(_directory);
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var vmId = Guid.NewGuid();

        log.Write(new AuditEntry(Time, "performVmAction", AuditOutcome.Requested, userId, "owner", deviceId, "Laptop", vmId, "Dev", Elevated: true, Detail: "action=TurnOff"));
        log.Write(new AuditEntry(Time, "performVmAction", AuditOutcome.Succeeded, Status: 202));

        var lines = Lines();
        Assert.Equal(2, lines.Count);
        var first = lines[0];
        Assert.Equal("performVmAction", (string?)first["action"]);
        Assert.Equal("requested", (string?)first["outcome"]);
        Assert.Equal(userId.ToString(), (string?)first["userId"]);
        Assert.Equal("owner", (string?)first["userName"]);
        Assert.Equal(deviceId.ToString(), (string?)first["deviceId"]);
        Assert.Equal("Laptop", (string?)first["deviceName"]);
        Assert.Equal(vmId.ToString(), (string?)first["vmId"]);
        Assert.Equal("Dev", (string?)first["vmName"]);
        Assert.True((bool?)first["elevated"]);
        Assert.Equal("action=TurnOff", (string?)first["detail"]);
        Assert.Equal("succeeded", (string?)lines[1]["outcome"]);
        Assert.Equal(202, (int?)lines[1]["status"]);
        Assert.False(lines[1].ContainsKey("vmId"));
    }

    [Fact]
    public void Write_RemovesControlCharactersAndCapsTheDetail()
    {
        var log = new FileAuditLog(_directory);

        log.Write(new AuditEntry(Time, "createPairingRequest", AuditOutcome.Requested, Detail: "deviceName=Evil\nFAKE ENTRY\r" + new string('x', 1000)));

        var detail = (string?)Assert.Single(Lines())["detail"];
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail!, char.IsControl);
        Assert.StartsWith("deviceName=EvilFAKE ENTRY", detail, StringComparison.Ordinal);
        Assert.Equal(FileAuditLog.MaxDetailLength + 1, detail!.Length);
    }

    [Fact]
    public void Write_PastTheSizeLimit_RollsOverToThePreviousFile()
    {
        var log = new FileAuditLog(_directory, maxBytes: 200);

        log.Write(new AuditEntry(Time, "first", AuditOutcome.Requested, Detail: new string('a', 200)));
        log.Write(new AuditEntry(Time, "second", AuditOutcome.Requested));

        Assert.Equal("second", (string?)Assert.Single(Lines())["action"]);
        var previous = File.ReadAllText(Path.Combine(_directory, FileAuditLog.PreviousFileName));
        Assert.Contains("\"first\"", previous, StringComparison.Ordinal);
    }

    [Fact]
    public void NewFile_HasTheRestrictedAcl()
    {
        new FileAuditLog(_directory).Write(new AuditEntry(Time, "test", AuditOutcome.Requested));

        var security = new FileInfo(Path.Combine(_directory, FileAuditLog.FileName)).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToHashSet();
        using var current = WindowsIdentity.GetCurrent();
        Assert.Subset(
            new HashSet<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null),
                current.User!,
            },
            allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), allowed);
    }

    [Fact]
    public void Write_WhenTheFileCannotBeOpened_ThrowsAuditUnavailable()
    {
        // A directory where the log file should be makes every open fail.
        Directory.CreateDirectory(Path.Combine(_directory, FileAuditLog.FileName));
        var log = new FileAuditLog(_directory);

        Assert.Throws<AuditUnavailableException>(() => log.Write(new AuditEntry(Time, "test", AuditOutcome.Requested)));
    }

    private List<JsonObject> Lines() => File.ReadAllText(Path.Combine(_directory, FileAuditLog.FileName))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonNode.Parse(line)!.AsObject())
        .ToList();
}
