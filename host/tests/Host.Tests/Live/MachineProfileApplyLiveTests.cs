using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Applies a machine profile to the throwaway VM an earlier MachineProfileLiveTests run kept (HH_MACHINE_KEEP=1):
/// previews it, shuts the VM down to change its processors, starts it, applies a setting in the guest, checks both, and
/// deletes the VM. Skipped unless HH_MACHINE_APPLY_LIVE=1 and HH_LIVE_GUEST_VM=&lt;that VM's id&gt; are set. About 5 minutes.
/// </summary>
public sealed class MachineProfileApplyLiveTests(ITestOutputHelper output)
{
    private const string VmName = "HyperHarbor-Test";

    [EnvironmentFact("HH_MACHINE_APPLY_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task Live_ApplyToTheKeptVm_ShutsDownChangesStartsAndSetsTheGuest()
    {
        var progress = Path.Combine(Path.GetTempPath(), "hyperharbor-machine-apply-progress.log");
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        var before = await host.GetJsonAsync($"/api/v1/vms/{vmId}/compute");
        var processors = (int)before["processorCount"]! == 2 ? 3 : 2;

        var profile = new JsonObject
        {
            ["name"] = "Live apply",
            ["os"] = "windows",
            ["compute"] = new JsonObject { ["processors"] = processors },
            ["tweaks"] = new JsonArray(new JsonObject { ["id"] = "explorer.showHiddenFiles" }),
            ["display"] = new JsonObject { ["guest"] = new JsonObject { ["wallpaper"] = new JsonObject { ["color"] = "#0B0D10" } } },
            ["account"] = new JsonObject(),
        };
        var saved = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/machine-profiles", profile);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        var profileId = (string)(await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;

        try
        {
            var preview = await host.GetJsonAsync($"/api/v1/vms/{vmId}/machine-profile-preview?profileId={profileId}");
            Log("Preview: " + preview.ToJsonString());
            var parts = preview["changes"]!.AsArray().Select(change => (string)change!["part"]!).Distinct().ToList();
            Assert.Equal(["compute", "software"], parts);
            Assert.Contains(preview["notes"]!.AsArray(), note => (string?)note!["section"] == "account");

            var started = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/machine-profile", new { profileId, parts, shutDownToApply = true });
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
            Assert.Empty(job["setupResult"]!["problems"]!.AsArray());

            var after = await host.GetJsonAsync($"/api/v1/vms/{vmId}/compute");
            Assert.Equal((processors, "running"), ((int)after["processorCount"]!, (string?)after["state"]));

            var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)!;
            var check = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(HiddenFilesScript), new { vmId, admin.UserName, admin.Password }, TimeSpan.FromMinutes(2), CancellationToken.None);
            Log("Guest: " + check!.ToJsonString());
            Assert.Equal("1", (string?)check["hidden"]);
            Assert.Equal("11 13 16", (string?)check["background"]);
            Assert.Equal("", (string?)check["wallpaper"]);
        }
        finally
        {
            await DeleteAsync(host, vmId, Log);
        }
    }

    private static async Task DeleteAsync(LiveHostHarness host, Guid vmId, Action<string> log)
    {
        log("Deleting the VM.");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while ((string?)(await host.GetJsonAsync($"/api/v1/vms/{vmId}"))["state"] != "off")
        {
            await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/actions", new { action = "turnOff" });
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The VM could not be turned off for deleting.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        var deleting = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/delete", new { deleteDisks = true, deleteCheckpoints = true, confirmName = VmName });
        var deletion = (await deleting.Content.ReadFromJsonAsync<JsonObject>())!;
        while ((string?)deletion["state"] == "running")
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            deletion = (JsonObject)await host.GetJsonAsync($"/api/v1/jobs/{deletion["id"]}");
        }

        log("Delete: " + deletion.ToJsonString());
    }

    /// <summary>Explorer's Hidden value in the Default user hive, which the setup profile writes.</summary>
    private const string HiddenFilesScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
        $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
        $session = New-PSSession -VMId $request.vmId -Credential $credential
        $values = Invoke-Command -Session $session -ScriptBlock {
            & reg.exe load 'HKU\HyperHarborCheck' 'C:\Users\Default\NTUSER.DAT' 2>&1 | Out-Null
            function Value($path, $name) {
                $key = [Microsoft.Win32.Registry]::Users.OpenSubKey("HyperHarborCheck\$path")
                $value = if ($key) { [string]$key.GetValue($name) } else { $null }
                if ($key) { $key.Close() }
                $value
            }
            $read = @(
                (Value 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'Hidden'),
                (Value 'Control Panel\Colors' 'Background'),
                (Value 'Control Panel\Desktop' 'WallPaper'))
            [GC]::Collect(); [GC]::WaitForPendingFinalizers()
            & reg.exe unload 'HKU\HyperHarborCheck' 2>&1 | Out-Null
            $read
        }
        Remove-PSSession $session
        # A value back from Invoke-Command carries remoting properties; the interpolation keeps only the text.
        @{ ok = $true; result = @{ hidden = "$($values[0])"; background = "$($values[1])"; wallpaper = "$($values[2])" } } | ConvertTo-Json -Compress -Depth 4
        """;
}
