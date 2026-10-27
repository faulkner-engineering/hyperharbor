using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Checks 0.2.4 on a real Windows guest through the harness host: creates HyperHarbor-Test from a machine profile whose
/// browser is Brave made the default with the "Keep Microsoft Edge out of the way" tweak, waits for Ready, reads the
/// default associations, Brave's ProgId, and the Edge policies in the guest, grows the disk while the VM runs (C: must
/// grow), then grows it again while it is off, and deletes the VM. Skipped unless HH_RELEASE024_LIVE=1 and HH_SPIKE_ISO
/// (a Windows image in HH_HARNESS_ISO_FOLDER) are set; HH_LIVE_GUEST_VM=&lt;id&gt; reuses a VM an earlier run left
/// (HH_MACHINE_KEEP=1 keeps it). About 40 minutes from scratch.
/// </summary>
public sealed class Release024LiveTests(ITestOutputHelper output)
{
    private const long Gb = 1024L * 1024 * 1024;

    [EnvironmentFact("HH_RELEASE024_LIVE", "HH_SPIKE_ISO")]
    public async Task Live_DefaultBrowser_EdgeTweak_AndDiskGrowth()
    {
        var progress = Path.Combine(Path.GetTempPath(), "hyperharbor-release024-progress.log");
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        Guid vmId;
        if (Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM") is { Length: > 0 } existing)
        {
            vmId = Guid.Parse(existing);
        }
        else
        {
            vmId = await CreateAsync(host, Log);
        }

        try
        {
            await MachineProfileLiveTests.WaitUntilReadyAsync(host, vmId, Log);
            var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)!;

            var browser = await GuestAsync(BrowserScript, vmId, admin);
            Log("Browser: " + browser.ToJsonString());
            Assert.Equal(@"C:\ProgramData\HyperHarbor\DefaultAssociations.xml", (string?)browser["policy"]);
            Assert.Contains("ProgId=\"BraveHTML\"", (string?)browser["associations"], StringComparison.Ordinal);
            Assert.True((bool)browser["braveProgId"]!, "BraveHTML is not registered for all users; Brave may have installed per user.");
            Assert.Equal(0, (int?)browser["startupBoost"]);
            Assert.Equal(1, (int?)browser["hideFirstRun"]);

            var disks = await host.GetJsonAsync($"/api/v1/vms/{vmId}/disks");
            Log("Disks: " + disks.ToJsonString());
            var disk = disks["disks"]![0]!;
            var path = (string)disk["path"]!;
            Assert.True((bool)disk["growWhileRunning"]!);
            var before = (long)disk["maximumSizeBytes"]!;
            var cBefore = (long)(await GuestAsync(DriveScript, vmId, admin))["size"]!;

            var online = await GrowAsync(host, vmId, path, (int)(before / Gb) + 8, Log);
            Assert.True((bool)online["partitionExtended"]!, (string?)online["note"]);
            var cAfter = (long)(await GuestAsync(DriveScript, vmId, admin))["size"]!;
            Log($"C: {cBefore / (double)Gb:0.0} GB to {cAfter / (double)Gb:0.0} GB while running.");
            Assert.True(cAfter >= cBefore + 7 * Gb);

            Log("Shutting down for the off-VM growth.");
            var shutdown = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/actions", new { action = "shutdown" });
            Assert.True(shutdown.IsSuccessStatusCode, await shutdown.Content.ReadAsStringAsync());
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            while ((string?)(await host.GetJsonAsync($"/api/v1/vms/{vmId}"))["state"] != "off" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            var offline = await GrowAsync(host, vmId, path, (int)(before / Gb) + 16, Log);
            if (Environment.IsPrivilegedProcess)
            {
                Assert.True((bool)offline["partitionExtended"]!, (string?)offline["note"]);
            }
            else
            {
                Assert.False((bool)offline["partitionExtended"]!);
                Assert.Contains("installed service", (string?)offline["note"], StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("HH_MACHINE_KEEP") != "1")
            {
                await MachineProfileLiveTests.DeleteAsync(host, vmId, Log);
            }
        }
    }

    private static async Task<Guid> CreateAsync(LiveHostHarness host, Action<string> log)
    {
        var iso = Environment.GetEnvironmentVariable("HH_SPIKE_ISO")!;
        var inspection = await host.GetJsonAsync($"/api/v1/isos/{Uri.EscapeDataString(iso)}/inspection");
        var editions = inspection["editions"]!.AsArray().Select(item => (string)item!).ToList();
        var edition = editions.FirstOrDefault(name => name.EndsWith(" Pro", StringComparison.Ordinal)) ?? editions[0];
        var profile = new JsonObject
        {
            ["name"] = "Release 0.2.4 live",
            ["os"] = "windows",
            ["account"] = new JsonObject { ["windows"] = new JsonObject { ["defaultEdition"] = edition } },
            ["firmware"] = new JsonObject { ["tpm"] = true },
            // Windows Setup refuses less than 4 GB (platform-notes.md, camera tests).
            ["compute"] = new JsonObject { ["processors"] = 2, ["memory"] = new JsonObject { ["startupMb"] = 4096, ["dynamic"] = false }, ["diskSizeGb"] = 40 },
            ["network"] = new JsonObject { ["switch"] = "Default Switch" },
            ["tweaks"] = new JsonArray(new JsonObject { ["id"] = "system.hideEdge" }),
            ["browser"] = new JsonObject { ["app"] = new JsonObject { ["id"] = "Brave.Brave" }, ["makeDefault"] = true },
        };

        var saved = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/machine-profiles", profile);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        var profileId = (string)(await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;

        log($"Creating HyperHarbor-Test from {profileId} with {iso}, edition {edition}.");
        var created = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/machine-profiles/{profileId}/vms", new { name = "HyperHarbor-Test", isoName = iso, acknowledgeWarnings = true });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var job = await MachineProfileLiveTests.WaitForJobAsync(host, (await created.Content.ReadFromJsonAsync<JsonObject>())!, log);
        Assert.Equal("succeeded", (string?)job["state"]);
        return Guid.Parse((string)job["vmId"]!);
    }

    private static async Task<JsonNode> GrowAsync(LiveHostHarness host, Guid vmId, string path, int sizeGb, Action<string> log)
    {
        log($"Growing to {sizeGb} GB.");
        var started = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/disks/grow", new { path, sizeGb, acknowledgeWarnings = true });
        Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var job = await MachineProfileLiveTests.WaitForJobAsync(host, (await started.Content.ReadFromJsonAsync<JsonObject>())!, log);
        Assert.True((string?)job["state"] == "succeeded", job.ToJsonString());
        Assert.Equal(sizeGb * Gb, (long)job["diskGrowResult"]!["maximumSizeBytes"]!);
        return job["diskGrowResult"]!;
    }

    private static async Task<JsonNode> GuestAsync(string script, Guid vmId, GuestCredential admin) =>
        (await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(script), new { vmId, admin.UserName, admin.Password }, TimeSpan.FromMinutes(2), CancellationToken.None))!;

    private const string Connect = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
        $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
        $session = New-PSSession -VMId $request.vmId -Credential $credential

        """;

    /// <summary>The default associations policy and file, Brave's machine-wide ProgId, and two Edge policies.</summary>
    private const string BrowserScript = Connect + """
        $result = Invoke-Command -Session $session -ScriptBlock {
            $system = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -ErrorAction SilentlyContinue
            $edge = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -ErrorAction SilentlyContinue
            $file = 'C:\ProgramData\HyperHarbor\DefaultAssociations.xml'
            [pscustomobject]@{
                policy = $system.DefaultAssociationsConfiguration
                associations = if (Test-Path $file) { [string](Get-Content $file -Raw) } else { $null }
                braveProgId = Test-Path 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\BraveHTML'
                startupBoost = $edge.StartupBoostEnabled
                hideFirstRun = $edge.HideFirstRunExperience
            }
        }
        Remove-PSSession $session
        @{ ok = $true; result = ($result | Select-Object policy, associations, braveProgId, startupBoost, hideFirstRun) } | ConvertTo-Json -Compress -Depth 4
        """;

    /// <summary>The size of the guest's C: partition.</summary>
    private const string DriveScript = Connect + """
        $size = Invoke-Command -Session $session -ScriptBlock { (Get-Partition -DriveLetter C).Size }
        Remove-PSSession $session
        @{ ok = $true; result = @{ size = [long]$size } } | ConvertTo-Json -Compress -Depth 4
        """;
}
