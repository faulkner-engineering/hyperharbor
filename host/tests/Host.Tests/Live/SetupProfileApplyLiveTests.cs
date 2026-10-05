using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Profiles;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Applies a small setup profile to a running Windows VM that the live harness created (its administrator credential
/// is in the harness data folder), then reads back what changed. Changes the VM: use a throwaway one.
/// HH_PROFILE_APPLY_LIVE=1 and HH_LIVE_GUEST_VM=&lt;VM id&gt;; HH_HARNESS_DATA as for <see cref="LiveHostHarness"/>.
/// </summary>
public sealed class SetupProfileApplyLiveTests(ITestOutputHelper output)
{
    private static readonly SetupProfile Small = new(
        "Live apply",
        null,
        [new("7zip")],
        new([new("Microsoft.BingNews")], null, [new("WorkFolders-Client")]),
        [new("explorer.showFileExtensions")],
        new(new("edge"), [new("edge:odfafepnkmbhccpbejgmiehpchacaeak", "uBlock Origin")], new Dictionary<string, string> { ["showHomeButton"] = "true" }));

    [EnvironmentFact("HH_PROFILE_APPLY_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task Live_ASmallProfile_Applies()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var data = Environment.GetEnvironmentVariable("HH_HARNESS_DATA") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarborHarness");
        var admin = new VmCredentialStore(data).Find(vmId) ?? throw new InvalidOperationException("No administrator credential for that VM.");
        var application = new SetupProfileApplication(new SetupProfilePlanner(Catalogs.Default), new PowerShellDirectProfileApplier());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await application.ApplyAsync(vmId, admin, Small, step => output.WriteLine($"{watch.Elapsed:mm\\:ss} {step}"), CancellationToken.None);
        output.WriteLine($"Applied {outcome.Applied}, restart needed {outcome.RestartNeeded}, {watch.Elapsed:mm\\:ss}");
        foreach (var problem in outcome.Problems)
        {
            output.WriteLine("Problem: " + problem);
        }

        Assert.Empty(outcome.Problems);
        Assert.Equal(new SetupProfilePlanner(Catalogs.Default).Plan(Small).ItemCount, outcome.Applied);

        var check = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(CheckScript), new { vmId, admin.UserName, admin.Password }, TimeSpan.FromMinutes(2), CancellationToken.None);
        output.WriteLine(check!.ToJsonString());
        Assert.True((bool)check["sevenZip"]!);
        Assert.Equal("0", (string?)check["hideFileExt"]);
        Assert.Equal("1", (string?)check["showHomeButton"]);
        Assert.Contains("normal_installed", (string?)check["extensionSettings"], StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole path: a setup profile saved through the API, a VM created with it and an unattended Windows install,
    /// and the watcher applying it before Ready. Creates HyperHarbor-Test2 (a throwaway; delete it afterwards) and
    /// shuts HyperHarbor-Test down first to leave memory. HH_PROFILE_E2E_LIVE=1 and HH_SPIKE_ISO=&lt;Windows image in the
    /// harness ISO folder&gt;; about 25 minutes.
    /// </summary>
    [EnvironmentFact("HH_PROFILE_E2E_LIVE", "HH_SPIKE_ISO")]
    public async Task Live_CreateWithASetupProfile_AppliesItBeforeReady()
    {
        const string name = "HyperHarbor-Test2";
        var progress = Path.Combine(Path.GetTempPath(), "hyperharbor-e2e-progress.log");
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        var vms = (await host.GetJsonAsync("/api/v1/vms")).AsArray();
        if (vms.FirstOrDefault(vm => (string?)vm!["name"] == "HyperHarbor-Test" && (string?)vm["state"] == "running") is { } running)
        {
            Log("Shutting HyperHarbor-Test down to leave memory.");
            (await host.Client.PostAsJsonAsync($"/api/v1/vms/{running["id"]}/actions", new { action = "shutdown" })).EnsureSuccessStatusCode();
        }

        var saved = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/setup-profiles", Small);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var profileId = (string)(await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;

        var iso = Environment.GetEnvironmentVariable("HH_SPIKE_ISO")!;
        var inspection = await host.GetJsonAsync($"/api/v1/isos/{Uri.EscapeDataString(iso)}/inspection");
        var editions = inspection["editions"]!.AsArray().Select(item => (string)item!).ToList();
        var edition = editions.FirstOrDefault(item => item.EndsWith(" Pro", StringComparison.Ordinal)) ?? editions[0];
        Log($"Creating {name} from {iso} ({edition}) with setup profile {profileId}.");
        var created = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/vms", new
        {
            name,
            isoName = iso,
            diskSizeGb = 64,
            processorCount = 4,
            startupMemoryMb = 4096,
            maximumMemoryMb = 6144,
            dynamicMemory = true,
            enableTpm = true,
            acknowledgeWarnings = true,
            install = new { profileId = "windows-workstation", windowsEdition = edition, computerName = "HHTEST2", setupProfileId = profileId },
        });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var job = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        while ((string?)job["state"] == "running")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            job = (JsonObject)await host.GetJsonAsync($"/api/v1/jobs/{job["id"]}");
        }

        Assert.Equal("succeeded", (string?)job["state"]);
        var vmId = Guid.Parse((string)job["vmId"]!);

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(120);
        var seen = new List<string>();
        JsonNode install;
        while (true)
        {
            install = await host.GetJsonAsync($"/api/v1/vms/{vmId}/install");
            var line = $"{install["state"]}: {install["step"]}";
            if (seen.Count == 0 || seen[^1] != line)
            {
                seen.Add(line);
                Log(line);
            }

            if ((string?)install["state"] is "ready" or "failed" or "canceled")
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "The install did not finish in two hours.");
            await Task.Delay(TimeSpan.FromSeconds(20));
        }

        Log("Install status: " + install.ToJsonString());
        Assert.Equal("ready", (string?)install["state"]);
        Assert.Contains(seen, line => line.StartsWith("applyingProfile", StringComparison.Ordinal));
        Assert.Equal("Live apply", (string?)install["setupProfileName"]);
        Assert.Empty(install["setupResult"]!["problems"]!.AsArray());

        var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)!;
        var check = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(CheckScript), new { vmId, admin.UserName, admin.Password }, TimeSpan.FromMinutes(2), CancellationToken.None);
        Log("Check: " + check!.ToJsonString());
        Assert.True((bool)check["sevenZip"]!);
        Assert.Equal("0", (string?)check["hideFileExt"]);
        Assert.Equal("1", (string?)check["showHomeButton"]);
    }

    private const string CheckScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 6 }
        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
            $result = Invoke-Command -Session $session -ScriptBlock {
                $ErrorActionPreference = 'Continue'
                & reg.exe load 'HKU\HyperHarborCheck' 'C:\Users\Default\NTUSER.DAT' 2>&1 | Out-Null
                $key = [Microsoft.Win32.Registry]::Users.OpenSubKey('HyperHarborCheck\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced')
                $hide = if ($key) { [string]$key.GetValue('HideFileExt') } else { $null }
                if ($key) { $key.Close() }
                [GC]::Collect(); [GC]::WaitForPendingFinalizers()
                & reg.exe unload 'HKU\HyperHarborCheck' 2>&1 | Out-Null
                $edge = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -ErrorAction SilentlyContinue
                @{
                    sevenZip = (Test-Path 'C:\Program Files\7-Zip\7z.exe')
                    hideFileExt = $hide
                    showHomeButton = [string]$edge.ShowHomeButton
                    extensionSettings = [string]$edge.ExtensionSettings
                }
            }
            Reply @{ ok = $true; result = $result }
        }
        catch { Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message } }
        finally { if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue } }
        """;
}
