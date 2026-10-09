using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Creates the throwaway VMs HyperHarbor-Cam1 and HyperHarbor-Cam2 (Windows, unattended install, low memory) through the
/// harness host and leaves them running, for testing several Remote Desktop sessions at once. Skipped unless
/// HH_CAMTEST_LIVE=1, HH_SPIKE_ISO (the image name), HH_HARNESS_ISO_FOLDER, and HH_HARNESS_VM_FOLDER are set.
/// Changes only the two VMs it names; an existing VM of that name is reused.
/// </summary>
public sealed class CameraTestVmsLiveTests(ITestOutputHelper output)
{
    private static readonly string[] VmNames = ["HyperHarbor-Cam1", "HyperHarbor-Cam2"];

    [EnvironmentFact("HH_CAMTEST_LIVE", "HH_SPIKE_ISO")]
    public async Task Live_CreateTwoLowMemoryVms()
    {
        var progress = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarborHarness", "camtest-progress.log");
        Directory.CreateDirectory(Path.GetDirectoryName(progress)!);
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        var iso = Environment.GetEnvironmentVariable("HH_SPIKE_ISO")!;
        var inspection = await host.GetJsonAsync($"/api/v1/isos/{Uri.EscapeDataString(iso)}/inspection");
        var editions = inspection["editions"]!.AsArray().Select(item => (string)item!).ToList();
        var edition = editions.FirstOrDefault(name => name.EndsWith(" Pro", StringComparison.Ordinal)) ?? editions[0];

        foreach (var name in VmNames)
        {
            await CreateIfMissingAsync(host, name, iso, edition, Log);
            await WaitUntilReadyAsync(host, name, Log);
            await ShrinkMemoryAsync(host, name, Log);
        }

        Log("Both VMs are ready.");
    }

    /// <summary>Windows 11 setup needs 4 GB; once installed, the VM is cut to 2 GB (shut down, apply, start).</summary>
    private static async Task ShrinkMemoryAsync(LiveHostHarness host, string name, Action<string> log)
    {
        var vm = (await host.GetJsonAsync("/api/v1/vms")).AsArray().First(item => (string?)item!["name"] == name)!;
        var response = await host.SendElevatedAsync(new HttpMethod("PATCH"), $"/api/v1/vms/{vm["id"]}/compute", new
        {
            startupMemoryMb = 2048,
            maximumMemoryMb = 2048,
            shutDownToApply = true,
            acknowledgeWarnings = true,
        });
        if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.OK))
        {
            throw new InvalidOperationException($"{name}: shrinking answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        // 202 carries a job (running VM); 200 means the settings were applied at once (VM off).
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var job = (JsonObject)(await response.Content.ReadFromJsonAsync<JsonObject>())!["job"]!;
            while ((string?)job["state"] == "running")
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                job = (JsonObject)await host.GetJsonAsync($"/api/v1/jobs/{job["id"]}");
            }

            if ((string?)job["state"] != "succeeded")
            {
                throw new InvalidOperationException($"{name}: shrinking failed: {job.ToJsonString()}");
            }
        }

        log($"{name}: now 2 GB.");

        // The VM may have been left off (an earlier run was interrupted); leave it running.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (true)
        {
            vm = (await host.GetJsonAsync("/api/v1/vms")).AsArray().First(item => (string?)item!["name"] == name)!;
            var state = (string?)vm["state"];
            if (state == "running")
            {
                return;
            }

            if (state == "off")
            {
                await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vm["id"]}/actions", new { action = "start" });
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{name}: did not start after shrinking.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task CreateIfMissingAsync(LiveHostHarness host, string name, string iso, string edition, Action<string> log)
    {
        var existing = (await host.GetJsonAsync("/api/v1/vms")).AsArray().FirstOrDefault(item => (string?)item!["name"] == name);
        if (existing is not null)
        {
            log($"{name} exists; reusing it.");
            return;
        }

        log($"Creating {name} from {iso} ({edition}), 4 GB for setup, then 2 GB.");
        var created = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/vms", new
        {
            name,
            isoName = iso,
            diskSizeGb = 40,
            processorCount = 2,
            startupMemoryMb = 4096,
            maximumMemoryMb = 4096,
            dynamicMemory = false,
            enableTpm = true,
            acknowledgeWarnings = true,
            install = new { profileId = "windows-workstation", windowsEdition = edition, computerName = name.Replace("HyperHarbor-", "HH").ToUpperInvariant() },
        });
        if (created.StatusCode != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException($"Create answered {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        }

        var job = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        while ((string?)job["state"] == "running")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            job = (JsonObject)await host.GetJsonAsync($"/api/v1/jobs/{job["id"]}");
        }

        if ((string?)job["state"] != "succeeded")
        {
            throw new InvalidOperationException($"Create failed: {job.ToJsonString()}");
        }
    }

    private static async Task WaitUntilReadyAsync(LiveHostHarness host, string name, Action<string> log)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(120);
        string? lastState = "";
        while (true)
        {
            var vm = (await host.GetJsonAsync("/api/v1/vms")).AsArray().First(item => (string?)item!["name"] == name)!;
            var state = (string?)vm["installState"];
            if (state != lastState)
            {
                log($"{name}: install state {state ?? "none"} (VM {vm["state"]}, provisioned {vm["provisioned"]}, id {vm["id"]}).");
                lastState = state;
            }

            if (state is null && (bool)vm["provisioned"]!)
            {
                return;
            }

            if (state is "failed" or "canceled")
            {
                var install = await host.GetJsonAsync($"/api/v1/vms/{vm["id"]}/install");
                throw new InvalidOperationException($"{name}: the install {state}: {install.ToJsonString()}");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{name}: the install did not finish in 120 minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }
}
