using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Phase 12b spike: creates (or reuses) the throwaway VM HyperHarbor-Test through the harness host with an unattended
/// Windows install, waits until it is ready, and tries each way of changing a guest that applying a setup profile
/// needs. Results go to spike-results.json in the harness data folder. Run elevated, with HH_SPIKE_LIVE=1,
/// HH_HARNESS_ISO_FOLDER, HH_HARNESS_VM_FOLDER, and HH_SPIKE_ISO (the image name). Changes only HyperHarbor-Test.
/// </summary>
public sealed class SetupProfileSpikeLiveTests(ITestOutputHelper output)
{
    private const string VmName = "HyperHarbor-Test";

    [EnvironmentFact("HH_SPIKE_LIVE", "HH_SPIKE_ISO")]
    public async Task Live_ApplySteps_WorkOverPowerShellDirect()
    {
        var progress = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarborHarness", "spike-progress.log");
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        var vmId = await EnsureInstalledVmAsync(host, Environment.GetEnvironmentVariable("HH_SPIKE_ISO")!, Log);

        var admin = new VmCredentialStore(host.DataDirectory).Find(vmId) ?? throw new InvalidOperationException("No administrator credential was stored.");
        Log($"Running the spike in {VmName} ({vmId}) as {admin.UserName}.");
        var result = await PowerShellDirectRunner.RunAsync(PowerShellDirectRunner.Encode(SpikeScript), new { vmId, admin.UserName, admin.Password }, TimeSpan.FromMinutes(40), CancellationToken.None);
        var json = result?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
        await File.WriteAllTextAsync(Path.Combine(host.DataDirectory, "spike-results.json"), json);
        Log("Spike results:" + Environment.NewLine + json);
    }

    /// <summary>The VM's id, after creating it with an unattended install if it does not exist, once it is ready.</summary>
    private static async Task<Guid> EnsureInstalledVmAsync(LiveHostHarness host, string iso, Action<string> log)
    {
        var vm = (await host.GetJsonAsync("/api/v1/vms")).AsArray().FirstOrDefault(item => (string?)item!["name"] == VmName);
        if (vm is null)
        {
            var inspection = await host.GetJsonAsync($"/api/v1/isos/{Uri.EscapeDataString(iso)}/inspection");
            var editions = inspection["editions"]!.AsArray().Select(item => (string)item!).ToList();
            var edition = editions.FirstOrDefault(name => name.EndsWith(" Pro", StringComparison.Ordinal)) ?? editions[0];
            log($"Creating {VmName} from {iso} ({edition}).");
            var created = await host.SendElevatedAsync(HttpMethod.Post, "/api/v1/vms", new
            {
                name = VmName,
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

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(90);
        string? lastState = "";
        while (true)
        {
            vm = (await host.GetJsonAsync("/api/v1/vms")).AsArray().First(item => (string?)item!["name"] == VmName)!;
            var state = (string?)vm["installState"];
            if (state != lastState)
            {
                log($"Install state: {state ?? "none"} (VM {vm["state"]}, provisioned {vm["provisioned"]}).");
                lastState = state;
            }

            if (state is null && (bool)vm["provisioned"]! && (string?)vm["state"] == "running")
            {
                return Guid.Parse((string)vm["id"]!);
            }

            if (state is "failed" or "canceled")
            {
                var install = await host.GetJsonAsync($"/api/v1/vms/{vm["id"]}/install");
                throw new InvalidOperationException($"The install {state}: {install.ToJsonString()}");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The install did not finish in 90 minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Each check reports what happened instead of stopping the others.</summary>
    private const string SpikeScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 6 }

        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
        }
        catch {
            Reply @{ ok = $false; stage = 'connect'; error = $_.Exception.Message }
            exit 0
        }

        try {
            $result = Invoke-Command -Session $session -ScriptBlock {
                $out = [ordered]@{}
                function Tail($text) { $t = ($text | Out-String).Trim(); if ($t.Length -gt 1500) { $t.Substring($t.Length - 1500) } else { $t } }
                function Check($name, [scriptblock]$block) {
                    try { $out[$name] = @{ ok = $true; detail = (Tail (& $block)) } }
                    catch { $out[$name] = @{ ok = $false; detail = $_.Exception.Message } }
                }

                $installer = Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1
                $winget = if ($installer) { Join-Path $installer.InstallLocation 'winget.exe' } else { $null }
                $out.wingetPath = $winget

                # Run 1 showed that winget.exe from the App Installer folder fails with "Access is denied" in a
                # PowerShell Direct session as an account that never signed in interactively.
                Check 'wingetDirect' { & $winget --version }

                # A one-shot scheduled task as SYSTEM, the way management tools run winget.
                Check 'wingetAsSystem' {
                    $dir = Join-Path $env:ProgramData 'HyperHarborSpike'
                    New-Item $dir -ItemType Directory -Force | Out-Null
                    $log = Join-Path $dir 'winget.log'
                    $codeFile = Join-Path $dir 'winget.code'
                    Remove-Item $log, $codeFile -ErrorAction SilentlyContinue
                    $command = "& '$winget' install --id 7zip.7zip --exact --scope machine --silent --accept-package-agreements --accept-source-agreements --disable-interactivity *> '$log'; Set-Content -Path '$codeFile' -Value `$LASTEXITCODE"
                    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
                    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
                    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
                    Register-ScheduledTask -TaskName 'HyperHarborSpike' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
                    Start-ScheduledTask -TaskName 'HyperHarborSpike'
                    $deadline = (Get-Date).AddMinutes(30)
                    while (-not (Test-Path $codeFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
                    Unregister-ScheduledTask -TaskName 'HyperHarborSpike' -Confirm:$false
                    "code $(Get-Content $codeFile -ErrorAction SilentlyContinue); installed $(Test-Path 'C:\Program Files\7-Zip\7z.exe'); " + (Get-Content $log -Raw -ErrorAction SilentlyContinue)
                }

                # Registering App Installer for this account, then winget in the session itself.
                Check 'registerAppInstaller' {
                    Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $installer.InstallLocation 'AppxManifest.xml')
                    $ErrorActionPreference = 'Continue'
                    (& $winget --version 2>&1 | Out-String) + " exit $LASTEXITCODE"
                }
                Check 'wingetExportAfterRegister' {
                    $ErrorActionPreference = 'Continue'
                    $exported = Join-Path $env:TEMP 'spike-export.json'
                    $text = & $winget export --output $exported --accept-source-agreements --disable-interactivity 2>&1 | Out-String
                    "exit $LASTEXITCODE; file $(Test-Path $exported); " + $text
                }

                Check 'appxRemove' {
                    $package = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq 'Microsoft.BingNews'
                    if (-not $package) { 'Microsoft.BingNews is not provisioned' }
                    else { Remove-AppxProvisionedPackage -Online -PackageName $package.PackageName | Out-Null; 'removed; still provisioned: ' + [bool](Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq 'Microsoft.BingNews') }
                }
                Check 'appxInstalledCopies' {
                    $copies = @(Get-AppxPackage -AllUsers -Name Microsoft.BingNews)
                    $copies | Remove-AppxPackage -AllUsers
                    "removed $($copies.Count) installed copies"
                }
                Check 'capability' {
                    $capability = Get-WindowsCapability -Online | Where-Object { $_.State -eq 'Installed' -and $_.Name -match '^(App\.StepsRecorder|Print\.Fax\.Scan|MathRecognizer)' } | Select-Object -First 1
                    if (-not $capability) { 'none of the test capabilities is installed' }
                    else { $removed = Remove-WindowsCapability -Online -Name $capability.Name; "$($capability.Name): restart needed $($removed.RestartNeeded)" }
                }
                Check 'feature' {
                    $feature = Get-WindowsOptionalFeature -Online -FeatureName 'WorkFolders-Client' -ErrorAction SilentlyContinue
                    if (-not $feature -or $feature.State -ne 'Enabled') { "WorkFolders-Client is $($feature.State)" }
                    else { $disabled = Disable-WindowsOptionalFeature -Online -FeatureName 'WorkFolders-Client' -NoRestart; "restart needed $($disabled.RestartNeeded)" }
                }
                Check 'defaultHive' {
                    $ErrorActionPreference = 'Continue'
                    $load = & reg.exe load 'HKU\HyperHarborSpike' 'C:\Users\Default\NTUSER.DAT' 2>&1 | Out-String
                    if ($LASTEXITCODE -ne 0) { throw "load: $load" }
                    $key = [Microsoft.Win32.Registry]::Users.CreateSubKey('HyperHarborSpike\Software\HyperHarborSpike')
                    $key.SetValue('Written', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
                    $key.Close()
                    $unloaded = $false
                    for ($i = 0; $i -lt 5 -and -not $unloaded; $i++) {
                        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
                        & reg.exe unload 'HKU\HyperHarborSpike' 2>&1 | Out-Null
                        $unloaded = $LASTEXITCODE -eq 0
                        if (-not $unloaded) { Start-Sleep -Seconds 1 }
                    }
                    "written; unloaded $unloaded"
                }
                Check 'edgePolicy' {
                    $root = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
                    New-Item -Path $root -Force | Out-Null
                    $settings = '{"odfafepnkmbhccpbejgmiehpchacaeak":{"installation_mode":"normal_installed","update_url":"https://edge.microsoft.com/extensionwebstorebase/v1/crx"}}'
                    Set-ItemProperty -Path $root -Name 'ExtensionSettings' -Value $settings -Type String
                    Set-ItemProperty -Path $root -Name 'EdgeShoppingAssistantEnabled' -Value 0 -Type DWord
                    'written'
                }
                $out
            }
            Reply @{ ok = $true; result = $result }
        }
        catch {
            Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;
}
