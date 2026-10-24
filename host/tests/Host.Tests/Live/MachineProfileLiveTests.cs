using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Creates the throwaway VM HyperHarbor-Test from a machine profile through the harness host, checks what the profile
/// set on the VM and inside the guest, and deletes the VM. Skipped unless HH_MACHINE_LIVE=1 and HH_SPIKE_ISO (a Windows
/// image in HH_HARNESS_ISO_FOLDER) are set; HH_MACHINE_GPU=1 adds a GPU share (needs a partitionable GPU), and
/// HH_MACHINE_KEEP=1 leaves the VM for a look, and HH_LIVE_GUEST_VM=&lt;id&gt; checks the guest of a VM an earlier run made
/// (and deletes it). About 30 minutes, or a few with HH_LIVE_GUEST_VM.
/// </summary>
public sealed class MachineProfileLiveTests(ITestOutputHelper output)
{
    private const string VmName = "HyperHarbor-Test";
    private const string WallpaperName = "live-wallpaper.bmp";

    /// <summary>A 64 by 36 BMP in the HyperHarbor amber, 24 bits per pixel, bottom-up rows padded to 4 bytes.</summary>
    internal static byte[] Wallpaper()
    {
        const int width = 64, height = 36, row = width * 3, pixels = row * height;
        var bmp = new byte[54 + pixels];
        void Write(int offset, int value) => BitConverter.GetBytes(value).CopyTo(bmp, offset);
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        Write(2, bmp.Length);
        Write(10, 54);
        Write(14, 40);
        Write(18, width);
        Write(22, height);
        bmp[26] = 1;
        bmp[28] = 24;
        Write(34, pixels);
        for (var offset = 54; offset < bmp.Length; offset += 3)
        {
            (bmp[offset], bmp[offset + 1], bmp[offset + 2]) = ((byte)0x2E, (byte)0x9B, (byte)0xE3);
        }

        return bmp;
    }

    [EnvironmentFact("HH_MACHINE_LIVE", "HH_SPIKE_ISO")]
    public async Task Live_CreateFromAMachineProfile_SetsTheVmAndTheGuest()
    {
        var progress = Path.Combine(Path.GetTempPath(), "hyperharbor-machine-progress.log");
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        var gpu = Environment.GetEnvironmentVariable("HH_MACHINE_GPU") == "1";
        var iso = Environment.GetEnvironmentVariable("HH_SPIKE_ISO")!;
        var inspection = await host.GetJsonAsync($"/api/v1/isos/{Uri.EscapeDataString(iso)}/inspection");
        var editions = inspection["editions"]!.AsArray().Select(item => (string)item!).ToList();
        var edition = editions.FirstOrDefault(name => name.EndsWith(" Pro", StringComparison.Ordinal)) ?? editions[0];
        Log($"Image {iso}, edition {edition}, GPU share {gpu}.");

        var profile = new JsonObject
        {
            ["name"] = "Live machine",
            ["os"] = "windows",
            ["account"] = new JsonObject { ["windows"] = new JsonObject { ["defaultEdition"] = edition } },
            ["display"] = new JsonObject
            {
                ["guest"] = new JsonObject
                {
                    ["theme"] = "dark",
                    ["accentColor"] = "#E39B2E",
                    ["wallpaper"] = new JsonObject { ["image"] = WallpaperName },
                },
            },
            ["firmware"] = new JsonObject { ["tpm"] = true },
            ["compute"] = new JsonObject
            {
                ["processors"] = 2,
                ["memory"] = new JsonObject { ["startupMb"] = 4096, ["dynamic"] = false },
                ["nestedVirtualization"] = true,
                ["diskSizeGb"] = 64,
            },
            ["network"] = new JsonObject { ["switch"] = "Default Switch" },
            ["tweaks"] = new JsonArray(new JsonObject { ["id"] = "explorer.showFileExtensions" }),
        };
        if (gpu)
        {
            profile["gpu"] = new JsonObject
            {
                ["enabled"] = true,
                ["share"] = new JsonObject { ["vramPercent"] = 25, ["encodePercent"] = 25, ["decodePercent"] = 25, ["computePercent"] = 25 },
            };
        }

        if (Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM") is { Length: > 0 } existing)
        {
            var reused = Guid.Parse(existing);
            try
            {
                await CheckGuestAsync(host, reused, gpu, Log);
            }
            finally
            {
                await DeleteAsync(host, reused, Log);
            }

            return;
        }

        var uploaded = await host.SendElevatedAsync(HttpMethod.Put, $"/api/v1/profile-images/{WallpaperName}", Wallpaper());
        Assert.True(uploaded.StatusCode == HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());
        var saved = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/machine-profiles", profile);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        var profileId = (string)(await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;

        Log($"Creating {VmName} from {profileId}.");
        var created = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/machine-profiles/{profileId}/vms", new { name = VmName, isoName = iso, acknowledgeWarnings = true });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var job = await WaitForJobAsync(host, (await created.Content.ReadFromJsonAsync<JsonObject>())!, Log);
        Assert.Equal("succeeded", (string?)job["state"]);
        var vmId = Guid.Parse((string)job["vmId"]!);

        try
        {
            var compute = await host.GetJsonAsync($"/api/v1/vms/{vmId}/compute");
            Log("Compute: " + compute.ToJsonString());
            Assert.Equal((2, 4096L, false, true), ((int)compute["processorCount"]!, (long)compute["startupMemoryMb"]!, (bool)compute["dynamicMemory"]!, (bool)compute["nestedVirtualization"]!));
            var firmware = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(FirmwareScript), new { vmId }, TimeSpan.FromMinutes(1), CancellationToken.None);
            Log("Firmware: " + firmware!.ToJsonString());
            Assert.True((bool)firmware["tpm"]!);
            Assert.True((bool)firmware["secureBoot"]!);
            if (gpu)
            {
                var performance = await host.GetJsonAsync($"/api/v1/vms/{vmId}/performance");
                Log("Performance: " + performance.ToJsonString());
                Assert.True((bool)performance["enabled"]!);
            }

            await WaitUntilReadyAsync(host, vmId, Log);
            await CheckGuestAsync(host, vmId, gpu, Log);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("HH_MACHINE_KEEP") != "1")
            {
                await DeleteAsync(host, vmId, Log);
            }
        }
    }

    /// <summary>The setup profile's values in the guest and, with a GPU share, the driver the guest setup copied.</summary>
    private static async Task CheckGuestAsync(LiveHostHarness host, Guid vmId, bool gpu, Action<string> log)
    {
        var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)!;
        var check = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(GuestScript), new { vmId, admin.UserName, admin.Password }, TimeSpan.FromMinutes(2), CancellationToken.None);
        log("Guest: " + check!.ToJsonString());

        if (gpu)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
            JsonNode performance;
            while ((performance = await host.GetJsonAsync($"/api/v1/vms/{vmId}/performance"))["driver"] is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
            }

            log("Performance after the guest setup: " + performance.ToJsonString());
            Assert.NotNull(performance["driver"]);
        }

        Assert.Equal("0", (string?)check["appsUseLightTheme"]);
        Assert.Equal("4281244643", (string?)check["accentColor"]);
        Assert.Equal("0", (string?)check["hideFileExt"]);
        Assert.Equal(ProfileImageStore.GuestPath(WallpaperName), (string?)check["wallpaper"]);
        Assert.Equal("10", (string?)check["wallpaperStyle"]);
        Assert.True((bool)check["wallpaperCopied"]!);
    }

    private static async Task<JsonObject> WaitForJobAsync(LiveHostHarness host, JsonObject job, Action<string> log)
    {
        var lastStep = "";
        while ((string?)job["state"] == "running")
        {
            if ((string?)job["step"] is { } step && step != lastStep)
            {
                log(step);
                lastStep = step;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
            job = (JsonObject)await host.GetJsonAsync($"/api/v1/jobs/{job["id"]}");
        }

        log("Job: " + job.ToJsonString());
        return job;
    }

    private static async Task WaitUntilReadyAsync(LiveHostHarness host, Guid vmId, Action<string> log)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(90);
        string? last = "";
        while (true)
        {
            var install = await host.GetJsonAsync($"/api/v1/vms/{vmId}/install");
            var state = (string?)install["state"];
            var step = $"{state}: {install["step"]}";
            if (step != last)
            {
                log("Install " + step);
                last = step;
            }

            if (state == "ready")
            {
                Assert.Empty(install["setupResult"]?["problems"]?.AsArray() ?? []);
                return;
            }

            if (state is "failed" or "canceled")
            {
                throw new InvalidOperationException("The install " + state + ": " + install.ToJsonString());
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The install did not finish in 90 minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    private static async Task DeleteAsync(LiveHostHarness host, Guid vmId, Action<string> log)
    {
        log("Deleting the VM.");

        // A job (the guest setup) can hold the VM for a while, so turning it off is retried until it is off.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while ((string?)(await host.GetJsonAsync($"/api/v1/vms/{vmId}"))["state"] != "off")
        {
            var turnOff = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/actions", new { action = "turnOff" });
            if (!turnOff.IsSuccessStatusCode)
            {
                log($"Turn off: {(int)turnOff.StatusCode} {await turnOff.Content.ReadAsStringAsync()}");
            }

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

    /// <summary>The VM's TPM and Secure Boot, read on the host.</summary>
    private const string FirmwareScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $vm = Get-VM -Id $request.vmId
        @{ ok = $true; result = @{
            tpm = [bool](Get-VMSecurity -VM $vm).TpmEnabled
            secureBoot = ((Get-VMFirmware -VM $vm).SecureBoot -eq 'On')
        } } | ConvertTo-Json -Compress -Depth 4
        """;

    /// <summary>The theme, accent, and file extension values in the Default user hive, which new accounts copy.</summary>
    private const string GuestScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
        $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
        $session = New-PSSession -VMId $request.vmId -Credential $credential
        $result = Invoke-Command -Session $session -ScriptBlock {
            $ErrorActionPreference = 'Continue'
            & reg.exe load 'HKU\HyperHarborCheck' 'C:\Users\Default\NTUSER.DAT' 2>&1 | Out-Null
            function Value($path, $name) {
                $key = [Microsoft.Win32.Registry]::Users.OpenSubKey("HyperHarborCheck\$path")
                $value = if ($key) { $key.GetValue($name) } else { $null }
                if ($key) { $key.Close() }
                if ($null -eq $value) { $null } elseif ($value -is [int]) { [string][uint32]([BitConverter]::ToUInt32([BitConverter]::GetBytes($value), 0)) } else { [string]$value }
            }
            $values = [pscustomobject]@{
                appsUseLightTheme = Value 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'AppsUseLightTheme'
                accentColor = Value 'Software\Microsoft\Windows\DWM' 'AccentColor'
                hideFileExt = Value 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'HideFileExt'
                wallpaper = Value 'Control Panel\Desktop' 'WallPaper'
                wallpaperStyle = Value 'Control Panel\Desktop' 'WallpaperStyle'
                wallpaperCopied = Test-Path 'C:\Users\Public\Pictures\HyperHarbor\live-wallpaper.bmp'
            }
            [GC]::Collect(); [GC]::WaitForPendingFinalizers()
            & reg.exe unload 'HKU\HyperHarborCheck' 2>&1 | Out-Null
            $values
        }
        Remove-PSSession $session
        @{ ok = $true; result = ($result | Select-Object appsUseLightTheme, accentColor, hideFileExt, wallpaper, wallpaperStyle, wallpaperCopied) } | ConvertTo-Json -Compress -Depth 4
        """;
}
