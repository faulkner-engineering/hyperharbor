using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Guest power requests through the real host (the harness), against a Windows VM it set up: Restart (Windows must boot
/// again), a compute change that shuts the guest down to apply and starts it again (the job path that waits on a guest
/// shutdown), and Shut down. Each request must be accepted well within the 60-second deadline. Changes the VM's
/// processor count: use a throwaway VM (MachineProfileLiveTests with HH_MACHINE_KEEP=1 makes one). Leaves it off.
/// HH_POWER_LIVE=1 and HH_LIVE_GUEST_VM=&lt;id&gt;. About 5 minutes.
/// </summary>
public sealed class PowerLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Accepted = TimeSpan.FromSeconds(30);

    [EnvironmentFact("HH_POWER_LIVE", "HH_LIVE_GUEST_VM")]
    public async Task Live_RestartAShutDownToApplyJobAndShutDown_ReachTheirStates()
    {
        var vmId = Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!);
        await using var host = await LiveHostHarness.StartAsync(output.WriteLine);
        var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)
            ?? throw new InvalidOperationException($"The harness has no administrator credential for {vmId}.");

        if ((string?)(await host.GetJsonAsync($"/api/v1/vms/{vmId}"))["state"] != "running")
        {
            await ActAsync(host, vmId, "start");
        }

        await WaitForStateAsync(host, vmId, "running", TimeSpan.FromMinutes(2));
        var boot = await BootTimeAsync(vmId, admin, TimeSpan.FromMinutes(5));
        output.WriteLine($"Booted at {boot}.");

        // 1. Restart: the request is accepted at once, and Windows boots again.
        await ActAsync(host, vmId, "restart");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        string rebooted;
        while ((rebooted = await BootTimeAsync(vmId, admin, TimeSpan.FromMinutes(5))) == boot)
        {
            Assert.True(DateTime.UtcNow < deadline, "Windows did not restart within 5 minutes.");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        output.WriteLine($"Restarted; booted at {rebooted}.");

        // 2. A setting that needs the VM off: the job shuts the guest down, applies it, and starts the VM again.
        var compute = await host.GetJsonAsync($"/api/v1/vms/{vmId}/compute");
        var processors = (int)compute["processorCount"]!;
        var wanted = processors > 1 ? processors - 1 : processors + 1;
        var update = await host.SendElevatedAsync(HttpMethod.Patch, $"/api/v1/vms/{vmId}/compute", new { processorCount = wanted, shutDownToApply = true });
        Assert.True(update.StatusCode == HttpStatusCode.Accepted, await update.Content.ReadAsStringAsync());
        var jobId = (string)(await update.Content.ReadFromJsonAsync<JsonObject>())!["job"]!["id"]!;
        var job = await WaitForJobAsync(host, jobId, TimeSpan.FromMinutes(6));
        output.WriteLine("Compute job: " + job.ToJsonString());
        Assert.Equal("succeeded", (string?)job["state"]);
        Assert.Equal(wanted, (int)(await host.GetJsonAsync($"/api/v1/vms/{vmId}/compute"))["processorCount"]!);
        await WaitForStateAsync(host, vmId, "running", TimeSpan.FromMinutes(2));

        // 3. Shut down: accepted at once, and the VM is off soon after.
        await BootTimeAsync(vmId, admin, TimeSpan.FromMinutes(5));
        await ActAsync(host, vmId, "shutdown");
        await WaitForStateAsync(host, vmId, "off", TimeSpan.FromMinutes(5));
    }

    /// <summary>Sends a power action and checks that the host accepted it quickly.</summary>
    private async Task ActAsync(LiveHostHarness host, Guid vmId, string action)
    {
        var watch = Stopwatch.StartNew();
        var response = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/actions", new { action });
        output.WriteLine($"{action}: {(int)response.StatusCode} in {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        Assert.True(watch.Elapsed < Accepted, $"{action} took {watch.Elapsed} to be accepted.");
    }

    private static async Task WaitForStateAsync(LiveHostHarness host, Guid vmId, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((string?)(await host.GetJsonAsync($"/api/v1/vms/{vmId}"))["state"] != state)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The VM did not reach {state} within {timeout}.");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<JsonNode> WaitForJobAsync(LiveHostHarness host, string jobId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonNode job;
        while ((string?)(job = await host.GetJsonAsync($"/api/v1/jobs/{jobId}"))["state"] == "running")
        {
            Assert.True(DateTime.UtcNow < deadline, $"The job did not finish within {timeout}.");
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return job;
    }

    /// <summary>When Windows in the guest last booted, once PowerShell Direct answers.</summary>
    private static async Task<string> BootTimeAsync(Guid vmId, GuestCredential admin, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                var result = await PowerShellDirectRunner.RunAsync(
                    PowerShellDirectRunner.Encode(BootTimeScript), new { vmId, admin.UserName, admin.Password }, TimeSpan.FromMinutes(2), CancellationToken.None);
                return (string)result!;
            }
            catch (Exception ex) when (ex is GuestUnavailableException or GuestOperationException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private const string BootTimeScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
        }
        catch {
            @{ ok = $false; stage = 'connect'; error = $_.Exception.Message } | ConvertTo-Json -Compress
            exit 0
        }
        try {
            # [string] drops the PSComputerName properties remoting adds, which would make ConvertTo-Json write an object.
            $boot = [string](Invoke-Command -Session $session -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o') })
            @{ ok = $true; result = $boot } | ConvertTo-Json -Compress
        }
        catch {
            @{ ok = $false; stage = 'guest'; error = $_.Exception.Message } | ConvertTo-Json -Compress
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;
}
