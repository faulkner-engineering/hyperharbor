using System.Text.Json;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Profiles;

/// <summary>
/// Capture against a real, running Windows VM that HyperHarbor has an administrator credential for (read from the
/// host's data folder, so run elevated). Read-only: nothing in the VM changes, and baselines go to a temporary folder.
/// HH_PROFILE_LIVE=1, HH_LIVE_GUEST_VM=&lt;VM id&gt;, and optionally HH_PROFILE_LIVE_OUT=&lt;file&gt; for the draft as JSON.
/// </summary>
public sealed class ProfileCaptureLiveTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _baselines = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_baselines, recursive: true);

    [EnvironmentFact("HH_PROFILE_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task Live_CaptureReadsTheVm()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var data = HostIdentityStore.DefaultDataDirectory;
        using var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddVmInventory().BuildServiceProvider();
        var inventory = services.GetRequiredService<IVmInventory>();
        var credentials = new VmCredentialStore(data);
        var reader = new PowerShellDirectProfileReader();
        var appx = new AppxInventoryService(inventory, credentials, reader, new AppxBaselineStore(_baselines), Catalogs.Default, TimeProvider.System, NullLogger<AppxInventoryService>.Instance);
        var users = new UserStore(data);
        var capture = new ProfileCaptureService(inventory, credentials, reader, appx, new ProvisioningStore(data), users, Catalogs.Default, NullLogger<ProfileCaptureService>.Instance);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var listed = await appx.ListAsync(vmId, CancellationToken.None);
        output.WriteLine($"Appx: {listed.Packages.Count} packages, build {listed.Build} {listed.Edition}, {watch.ElapsedMilliseconds} ms");
        Assert.NotEmpty(listed.Packages);

        watch.Restart();
        var draft = await capture.CaptureAsync(vmId, users.GetOrCreateDefault().UserId, CancellationToken.None);
        output.WriteLine($"Capture: {watch.ElapsedMilliseconds} ms");
        var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true });
        output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("HH_PROFILE_LIVE_OUT") is { Length: > 0 } file)
        {
            await File.WriteAllTextAsync(file, json);
        }

        Assert.Equal(listed.Build, draft.Build);
    }
}
