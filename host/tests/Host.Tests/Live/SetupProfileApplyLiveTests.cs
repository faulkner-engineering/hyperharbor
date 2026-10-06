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

    /// <summary>
    /// Part 2: a profile applied to an existing VM through POST /vms/{id}/setup-profile. Creates HyperHarbor-Test with
    /// an unattended install (no setup profile), gives the User's account a profile in it (as if it had signed in),
    /// applies a profile with a feature removal and a restart, checks the account's own settings, and deletes the VM.
    /// HH_PROFILE_EXISTING_LIVE=1 and HH_SPIKE_ISO=&lt;Windows image in the harness ISO folder&gt;; about 25 minutes, or a
    /// few with HH_LIVE_GUEST_VM=&lt;id of HyperHarbor-Test from an earlier run&gt;.
    /// </summary>
    [EnvironmentFact("HH_PROFILE_EXISTING_LIVE", "HH_SPIKE_ISO")]
    public async Task Live_ApplyToAnExistingVm_ChangesTheAccountAndRestarts()
    {
        const string name = "HyperHarbor-Test";
        var progress = Path.Combine(Path.GetTempPath(), "hyperharbor-existing-progress.log");
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        // HH_LIVE_GUEST_VM reuses a VM from an earlier run instead of installing a new one.
        var vmId = Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM") is { Length: > 0 } existing
            ? Guid.Parse(existing)
            : await CreateInstalledVmAsync(host, name, Environment.GetEnvironmentVariable("HH_SPIKE_ISO")!, Log);
        var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)!;

        var prepared = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(PrepareScript), new { vmId, admin.UserName, admin.Password, account = "hh-owner" }, TimeSpan.FromMinutes(3), CancellationToken.None);
        Log("Prepared: " + prepared!.ToJsonString());
        var features = prepared["features"] is JsonArray found ? found.Select(item => (string)item!).ToList() : [];
        var feature = features.FirstOrDefault();

        var profile = Small with { Remove = new([new("Microsoft.BingNews")], null, feature is null ? null : [new(feature)]) };
        var saved = await host.SendElevatedAsync(HttpMethod.Put, "/api/v1/setup-profiles/existing-apply", profile with { Name = "Existing apply" });
        if (saved.StatusCode == HttpStatusCode.NotFound)
        {
            saved = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/setup-profiles", profile with { Name = "Existing apply" });
        }

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var profileId = (string)(await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;

        Log($"Applying {profileId} (feature removal: {feature ?? "none"}).");
        var started = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/setup-profile", new { profileId, restartIfNeeded = true });
        Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var job = (await started.Content.ReadFromJsonAsync<JsonObject>())!;
        var lastStep = "";
        while ((string?)job["state"] == "running")
        {
            if ((string?)job["step"] is { } step && step != lastStep)
            {
                Log(step);
                lastStep = step;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
            job = (JsonObject)await host.GetJsonAsync($"/api/v1/jobs/{job["id"]}");
        }

        Log("Job: " + job.ToJsonString());
        Assert.Equal("succeeded", (string?)job["state"]);
        var result = job["setupResult"]!;
        Assert.Empty(result["problems"]!.AsArray());
        Assert.False((bool)result["restartPending"]!);

        var check = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(CheckScript), new { vmId, admin.UserName, admin.Password, account = "hh-owner" }, TimeSpan.FromMinutes(2), CancellationToken.None);
        Log("Check: " + check!.ToJsonString());
        Assert.True((bool)check["sevenZip"]!);
        Assert.Equal("0", (string?)check["hideFileExt"]);
        Assert.Equal("0", (string?)check["accountHideFileExt"]);

        Log("Deleting the VM.");
        (await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/actions", new { action = "turnOff" })).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(5));
        var deleting = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/delete", new { deleteDisks = true, deleteCheckpoints = true, confirmName = name });
        Assert.True(deleting.StatusCode == HttpStatusCode.Accepted, await deleting.Content.ReadAsStringAsync());

        // The harness host stops with the test, so the delete job must finish first.
        var deletion = (await deleting.Content.ReadFromJsonAsync<JsonObject>())!;
        while ((string?)deletion["state"] == "running")
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            deletion = (JsonObject)await host.GetJsonAsync($"/api/v1/jobs/{deletion["id"]}");
        }

        Log("Delete: " + deletion.ToJsonString());
        Assert.Equal("succeeded", (string?)deletion["state"]);
    }

    private static async Task<Guid> CreateInstalledVmAsync(LiveHostHarness host, string name, string iso, Action<string> log)
    {
        var inspection = await host.GetJsonAsync($"/api/v1/isos/{Uri.EscapeDataString(iso)}/inspection");
        var editions = inspection["editions"]!.AsArray().Select(item => (string)item!).ToList();
        var edition = editions.FirstOrDefault(item => item.EndsWith(" Pro", StringComparison.Ordinal)) ?? editions[0];
        log($"Creating {name} from {iso} ({edition}).");
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
            install = new { profileId = "windows-workstation", windowsEdition = edition, computerName = "HHTEST" },
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
        var last = "";
        while (true)
        {
            var install = await host.GetJsonAsync($"/api/v1/vms/{vmId}/install");
            var line = $"{install["state"]}: {install["step"]}";
            if (line != last)
            {
                log(line);
                last = line;
            }

            if ((string?)install["state"] == "ready")
            {
                return vmId;
            }

            Assert.False((string?)install["state"] is "failed" or "canceled", install.ToJsonString());
            Assert.True(DateTime.UtcNow < deadline, "The install did not finish in two hours.");
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// Gives the account a profile without signing in (userenv CreateProfile), as after a first sign-in, and lists which
    /// of a few harmless optional features are enabled, so the test can remove one.
    /// </summary>
    private const string PrepareScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 6 }
        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
            $result = Invoke-Command -Session $session -ArgumentList $request.account -ScriptBlock {
                param($account)
                Add-Type -Namespace HyperHarborLive -Name UserEnv -MemberDefinition '[DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int CreateProfile(string sid, string name, System.Text.StringBuilder path, uint size);'
                $sid = (Get-LocalUser -Name $account).SID.Value
                $path = New-Object System.Text.StringBuilder 260
                $code = [HyperHarborLive.UserEnv]::CreateProfile($sid, $account, $path, 260)
                $features = @('WindowsMediaPlayer', 'SmbDirect', 'Printing-XPSServices-Features', 'WorkFolders-Client') |
                    Where-Object { [string](Get-WindowsOptionalFeature -Online -FeatureName $_ -ErrorAction SilentlyContinue).State -like 'Enabled*' }
                @{ createProfile = ('0x{0:X8}' -f $code); profile = $path.ToString(); features = @($features) }
            }
            Reply @{ ok = $true; result = $result }
        }
        catch { Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message } }
        finally { if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue } }
        """;

    private const string CheckScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 6 }
        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
            $result = Invoke-Command -Session $session -ArgumentList $request.account -ScriptBlock {
                param($account)
                $ErrorActionPreference = 'Continue'
                function HideFileExt($file) {
                    & reg.exe load 'HKU\HyperHarborCheck' $file 2>&1 | Out-Null
                    $key = [Microsoft.Win32.Registry]::Users.OpenSubKey('HyperHarborCheck\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced')
                    $value = if ($key) { [string]$key.GetValue('HideFileExt') } else { $null }
                    if ($key) { $key.Close() }
                    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
                    & reg.exe unload 'HKU\HyperHarborCheck' 2>&1 | Out-Null
                    $value
                }
                $hide = HideFileExt 'C:\Users\Default\NTUSER.DAT'
                $accountHide = $null
                if ($account) {
                    $sid = (Get-LocalUser -Name $account).SID.Value
                    $userProfile = Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $sid } | Select-Object -First 1
                    if ($userProfile) { $accountHide = HideFileExt (Join-Path $userProfile.LocalPath 'NTUSER.DAT') }
                }
                $edge = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -ErrorAction SilentlyContinue
                @{
                    sevenZip = (Test-Path 'C:\Program Files\7-Zip\7z.exe')
                    hideFileExt = $hide
                    accountHideFileExt = $accountHide
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
