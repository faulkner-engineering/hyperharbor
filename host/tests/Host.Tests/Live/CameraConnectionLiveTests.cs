using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// For camera redirection tests with several VMs. Asks the harness host for Remote Desktop credentials for the named
/// test VMs and writes them to the file in HH_CAMCONN_OUT (the file holds passwords: delete it afterwards). Then it
/// stays up and answers guest queries until HH_CAMCONN_OUT + ".done" exists: a script placed in
/// HH_CAMCONN_OUT + ".query" (first line the VM name, the rest PowerShell) runs in that VM over PowerShell Direct with
/// the stored administrator credential, and its output is written to HH_CAMCONN_OUT + ".result". Skipped unless
/// HH_CAMCONN_LIVE=1 and HH_CAMCONN_OUT are set. HH_CAMCONN_VMS is a comma-separated list of VM names (default
/// HyperHarbor-Cam1,HyperHarbor-Cam2). Only throwaway test VMs should be named.
/// </summary>
public sealed class CameraConnectionLiveTests(ITestOutputHelper output)
{
    private const string GuestScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
            $text = Invoke-Command -Session $session -ScriptBlock ([scriptblock]::Create($request.command)) | Out-String
            Remove-PSSession $session
            @{ ok = $true; result = $text.Trim() } | ConvertTo-Json -Compress
        }
        catch {
            @{ ok = $false; stage = 'run'; error = ($_ | Out-String) } | ConvertTo-Json -Compress
        }
        """;

    [EnvironmentFact("HH_CAMCONN_LIVE", "HH_CAMCONN_OUT")]
    public async Task Live_ServeConnectionsAndGuestQueries()
    {
        var names = (Environment.GetEnvironmentVariable("HH_CAMCONN_VMS") ?? "HyperHarbor-Cam1,HyperHarbor-Cam2")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var target = Environment.GetEnvironmentVariable("HH_CAMCONN_OUT")!;
        void Log(string line) => output.WriteLine(line);

        await using var host = await LiveHostHarness.StartAsync(Log);
        var vms = (await host.GetJsonAsync("/api/v1/vms")).AsArray();
        var ids = new Dictionary<string, Guid>();
        var result = new JsonObject();
        foreach (var name in names)
        {
            var vm = vms.FirstOrDefault(item => (string?)item!["name"] == name)
                ?? throw new InvalidOperationException($"No VM named {name}.");
            ids[name] = Guid.Parse((string)vm["id"]!);
            var response = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vm["id"]}/connect");
            if (!response.IsSuccessStatusCode)
            {
                // Keep serving the other VMs; the error text says why this one cannot connect.
                result[name] = new JsonObject { ["error"] = $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}" };
                Log($"{name}: connect failed ({(int)response.StatusCode}).");
                continue;
            }

            var connection = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            connection["vmId"] = (string?)vm["id"];
            result[name] = connection;
            Log($"{name}: {connection["userName"]}@{connection["address"]}:{connection["port"]}");
        }

        await File.WriteAllTextAsync(target, result.ToJsonString());
        var credentials = new VmCredentialStore(host.DataDirectory);

        var query = target + ".query";
        var answer = target + ".result";
        var done = target + ".done";
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(45);
        while (!File.Exists(done) && DateTime.UtcNow < deadline)
        {
            if (File.Exists(query))
            {
                var lines = (await File.ReadAllTextAsync(query)).Split('\n', 2);
                File.Delete(query);
                var name = lines[0].Trim();
                JsonNode? reply;
                try
                {
                    var admin = ids.TryGetValue(name, out var id) ? credentials.Find(id) : null;
                    reply = admin is null
                        ? new JsonObject { ["ok"] = false, ["output"] = $"No stored credential for {name}." }
                        : await PowerShellDirectRunner.RunAsync(
                            PowerShellDirectRunner.Encode(GuestScript),
                            new { vmId = ids[name], admin.UserName, admin.Password, command = lines.ElementAtOrDefault(1) ?? "" },
                            TimeSpan.FromMinutes(3),
                            CancellationToken.None);
                }
                catch (Exception error)
                {
                    reply = new JsonObject { ["ok"] = false, ["output"] = error.Message };
                }

                await File.WriteAllTextAsync(answer, reply?.ToJsonString() ?? "null");
            }

            await Task.Delay(500);
        }
    }
}
