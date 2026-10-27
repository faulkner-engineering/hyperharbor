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

        var fresh = Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM") is not { Length: > 0 };
        try
        {
            await MachineProfileLiveTests.WaitUntilReadyAsync(host, vmId, Log);
            var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)!;

            if (fresh)
            {
                // The autounattend puts the recovery partition before C:, and Setup puts Windows RE in it.
                var layout = await GuestAsync(LayoutScript, vmId, admin);
                Log("Layout: " + layout.ToJsonString());
                Assert.Equal("Recovery", (string?)layout["partition3"]);
                Assert.Equal("C", (string?)layout["lastLetter"]);
                Assert.Contains(@"partition3\Recovery\WindowsRE", (string?)layout["winre"], StringComparison.OrdinalIgnoreCase);
            }

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
                // The host mounted the disk and extended C: (C: is last on a fresh layout); check it in the guest.
                Assert.True((bool)offline["partitionExtended"]!, (string?)offline["note"]);
                var started = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/actions", new { action = "start" });
                Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
                var cOffline = await WhenGuestAnswersAsync(DriveScript, vmId, admin);
                Log($"C: {cAfter / (double)Gb:0.0} GB to {(long)cOffline["size"]! / (double)Gb:0.0} GB while off.");
                Assert.True((long)cOffline["size"]! >= cAfter + 7 * Gb);
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

    /// <summary>
    /// Installs Ubuntu as HyperHarbor-Test, grows its disk while it runs, and checks that the result gives the commands
    /// to extend the file system instead of touching it. Skipped unless HH_RELEASE024_LINUX=1 and HH_LINUX_ISO (an Ubuntu
    /// server image in HH_HARNESS_ISO_FOLDER) are set. The test types "yes" at Ubuntu's "Continue with autoinstall?"
    /// prompt, which a person does in the console otherwise. About 20 minutes.
    /// </summary>
    [EnvironmentFact("HH_RELEASE024_LINUX", "HH_LINUX_ISO")]
    public async Task Live_LinuxDiskGrowth_GivesTheCommands()
    {
        void Log(string line) => output.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        await using var host = await LiveHostHarness.StartAsync(Log);
        var profile = new JsonObject
        {
            ["name"] = "Release 0.2.4 Linux live",
            ["os"] = "linux",
            ["account"] = new JsonObject { ["linux"] = new JsonObject { ["installDesktop"] = true } },
            ["compute"] = new JsonObject { ["processors"] = 2, ["memory"] = new JsonObject { ["startupMb"] = 2048, ["dynamic"] = false }, ["diskSizeGb"] = 20 },
            ["network"] = new JsonObject { ["switch"] = "Default Switch" },
        };
        var saved = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/machine-profiles", profile);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        var profileId = (string)(await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        var created = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/machine-profiles/{profileId}/vms",
            new { name = "HyperHarbor-Test", isoName = Environment.GetEnvironmentVariable("HH_LINUX_ISO"), acknowledgeWarnings = true });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var job = await MachineProfileLiveTests.WaitForJobAsync(host, (await created.Content.ReadFromJsonAsync<JsonObject>())!, Log);
        Assert.Equal("succeeded", (string?)job["state"]);
        var vmId = Guid.Parse((string)job["vmId"]!);

        try
        {
            await ConfirmAutoinstallAsync(host, vmId, Log);
            await MachineProfileLiveTests.WaitUntilReadyAsync(host, vmId, Log);
            var disk = (await host.GetJsonAsync($"/api/v1/vms/{vmId}/disks"))["disks"]![0]!;
            var result = await GrowAsync(host, vmId, (string)disk["path"]!, 24, Log);

            Assert.False((bool)result["partitionExtended"]!);
            Assert.Contains("growpart", (string?)result["note"], StringComparison.Ordinal);
        }
        finally
        {
            await MachineProfileLiveTests.DeleteAsync(host, vmId, Log);
        }
    }

    /// <summary>
    /// Answers "Continue with autoinstall?" at the VM's keyboard. The host reports awaitingConfirmation from the start,
    /// since it cannot see the console, so typing at once lands in GRUB's menu ("e" opens its editor, and the boot stops
    /// there; seen 2026-10-26). The state stays awaitingConfirmation until the installed system reports itself, so it
    /// cannot tell when the answer was taken either. The menu times out after 30 seconds, so this types "yes" three, five,
    /// and seven minutes after the start; once the install runs, the extra keys only scroll its log.
    /// </summary>
    private static async Task ConfirmAutoinstallAsync(LiveHostHarness host, Guid vmId, Action<string> log)
    {
        foreach (var wait in new[] { 3, 2, 2 })
        {
            await Task.Delay(TimeSpan.FromMinutes(wait));
            if ((string?)(await host.GetJsonAsync($"/api/v1/vms/{vmId}/install"))["state"] != "awaitingConfirmation")
            {
                return;
            }

            log("Answering yes at the autoinstall prompt.");
            await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(TypeYesScript), new { vmId }, TimeSpan.FromMinutes(1), CancellationToken.None);
        }
    }

    /// <summary>Runs a guest script, retrying for a few minutes while the guest is still starting.</summary>
    private static async Task<JsonNode> WhenGuestAnswersAsync(string script, Guid vmId, GuestCredential admin)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (true)
        {
            try
            {
                return await GuestAsync(script, vmId, admin);
            }
            // While Windows starts, PowerShell Direct fails either way: unavailable, or "a remote session might have ended".
            catch (Exception ex) when (ex is GuestUnavailableException or GuestOperationException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    }

    internal static async Task<Guid> CreateAsync(LiveHostHarness host, Action<string> log)
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

    /// <summary>What partition 3 is, which drive letter the last partition has, and where Windows RE lives.</summary>
    private const string LayoutScript = Connect + """
        $result = Invoke-Command -Session $session -ScriptBlock {
            $partitions = @(Get-Partition -DiskNumber 0 | Sort-Object Offset)
            [pscustomobject]@{
                partition3 = [string]($partitions | Where-Object PartitionNumber -eq 3).Type
                lastLetter = [string]$partitions[-1].DriveLetter
                winre = [string]((reagentc.exe /info | Out-String) -split "`n" | Where-Object { $_ -match 'Windows RE location' })
            }
        }
        Remove-PSSession $session
        @{ ok = $true; result = ($result | Select-Object partition3, lastLetter, winre) } | ConvertTo-Json -Compress -Depth 4
        """;

    /// <summary>
    /// Types "yes" and Enter at the VM's console (host side, Msvm_Keyboard). One key at a time: TypeText's letters do
    /// not reach the Ubuntu installer's console, though its Enter does (2026-10-27).
    /// </summary>
    private const string TypeYesScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $vm = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "Name='$($request.vmId)'"
        $keyboard = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_Keyboard
        foreach ($key in 0x59, 0x45, 0x53, 0x0D) {
            Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = [uint32]$key } | Out-Null
            Start-Sleep -Milliseconds 300
        }
        @{ ok = $true; result = $null } | ConvertTo-Json -Compress
        """;

    /// <summary>The size of the guest's C: partition.</summary>
    private const string DriveScript = Connect + """
        $size = Invoke-Command -Session $session -ScriptBlock { (Get-Partition -DriveLetter C).Size }
        Remove-PSSession $session
        @{ ok = $true; result = @{ size = [long]$size } } | ConvertTo-Json -Compress -Depth 4
        """;
}
