using System.IO.Pipes;
using System.Security.Principal;
using HyperHarbor.Host.Tray;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>The tray's check that the pipe it connects to can be the service's (pipe squatting).</summary>
public sealed class PipeServerCheckTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    [Fact]
    public void TheServicesOwners_AndTheTraysOwnUser_AreTrusted()
    {
        Assert.True(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), Me));
        Assert.True(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), Me));
        Assert.True(PipeServerCheck.IsTrustedOwner(Me, Me));
    }

    [Fact]
    public void AnotherAccount_OrNoOwner_IsNotTrusted()
    {
        // Another local user, a service that is not LocalSystem, and the Users group.
        Assert.False(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), Me));
        Assert.False(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), Me));
        Assert.False(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), Me));
        Assert.False(PipeServerCheck.IsTrustedOwner(null, Me));
    }

    [Fact]
    public async Task APipeThisAccountServes_IsTrusted()
    {
        // Owned by this user, or by Administrators when the tests run elevated (CI).
        var name = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        await accepted;

        Assert.True(PipeServerCheck.IsTrusted(client));
    }
}
