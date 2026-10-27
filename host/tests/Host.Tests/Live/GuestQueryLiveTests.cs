using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// For investigations: runs the PowerShell in the file HH_GUEST_QUERY in a Windows VM the harness set up, as its
/// administrator over PowerShell Direct, or with HH_GUEST_QUERY_USER=&lt;account&gt; in that account's signed-in session
/// (as <see cref="SessionLiveTests"/> does), and prints the value it returns as JSON. The credential comes from the
/// harness data folder (or HH_GUEST_ADMIN_DATA). HH_GUEST_QUERY_LIVE=1 and HH_LIVE_GUEST_VM=&lt;id&gt;.
/// </summary>
public sealed class GuestQueryLiveTests(ITestOutputHelper output)
{
    [EnvironmentFact("HH_GUEST_QUERY_LIVE", "HH_LIVE_GUEST_VM", "HH_GUEST_QUERY")]
    public async Task Live_RunQuery()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var data = Environment.GetEnvironmentVariable("HH_GUEST_ADMIN_DATA") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarborHarness");
        var admin = new VmCredentialStore(data).Find(vmId) ?? throw new InvalidOperationException($"No administrator credential for {vmId} in {data}.");
        var guest = new SessionLiveTests.Guest(vmId, admin);
        var script = await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("HH_GUEST_QUERY")!);

        var result = Environment.GetEnvironmentVariable("HH_GUEST_QUERY_USER") is { Length: > 0 } user
            ? await guest.AsUserAsync(user, script, TimeSpan.FromMinutes(3))
            : await guest.AsAdminAsync(script);
        output.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
