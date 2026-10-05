using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// <see cref="IGuestProfileApplier"/> over PowerShell Direct, as the VM's stored administrator. Each step has its own
/// script, to stay under the command line limit (see <see cref="PowerShellDirectRunner"/>), and reports every item.
/// </summary>
public sealed class PowerShellDirectProfileApplier : IGuestProfileApplier
{
    private static readonly TimeSpan PackagesTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SettingsTimeout = TimeSpan.FromMinutes(5);
    private static readonly string EncodedPackagesScript = PowerShellDirectRunner.Encode(PackagesScript);
    private static readonly string EncodedRemoveScript = PowerShellDirectRunner.Encode(RemoveScript);
    private static readonly string EncodedSettingsScript = PowerShellDirectRunner.Encode(SettingsScript);

    public async Task<IReadOnlyList<ApplyItemResult>> InstallPackagesAsync(Guid vmId, GuestCredential admin, IReadOnlyList<PackageInstall> packages, CancellationToken cancellationToken)
    {
        var result = await PowerShellDirectRunner.RunAsync(EncodedPackagesScript, new
        {
            vmId,
            admin.UserName,
            admin.Password,
            packages = packages.Select(package => new
            {
                item = package.Item,
                id = package.Id,
                source = package.Source == PackageSource.MicrosoftStore ? "msstore" : "winget",
            }),
        }, PackagesTimeout, cancellationToken).ConfigureAwait(false);
        return ParseItems(result);
    }

    public async Task<IReadOnlyList<ApplyItemResult>> RemoveAsync(
        Guid vmId,
        GuestCredential admin,
        IReadOnlyList<ProfileItem> appx,
        IReadOnlyList<ProfileItem> capabilities,
        IReadOnlyList<ProfileItem> features,
        CancellationToken cancellationToken)
    {
        static object Items(IReadOnlyList<ProfileItem> items) => items.Select(item => new { id = item.Id, item = item.Name ?? item.Id });
        var result = await PowerShellDirectRunner.RunAsync(EncodedRemoveScript, new
        {
            vmId,
            admin.UserName,
            admin.Password,
            appx = Items(appx),
            capabilities = Items(capabilities),
            features = Items(features),
        }, RemoveTimeout, cancellationToken).ConfigureAwait(false);
        return ParseItems(result);
    }

    public async Task<IReadOnlyList<ApplyItemResult>> WriteSettingsAsync(Guid vmId, GuestCredential admin, IReadOnlyList<RegistryWrite> writes, CancellationToken cancellationToken)
    {
        var result = await PowerShellDirectRunner.RunAsync(EncodedSettingsScript, new
        {
            vmId,
            admin.UserName,
            admin.Password,
            writes = writes.Select(write => new
            {
                item = write.Item,
                target = write.Target == RegistryTarget.DefaultUser ? "default" : "machine",
                key = write.Key,
                name = write.Name,
                type = write.Type,
                value = write.Value,
            }),
        }, SettingsTimeout, cancellationToken).ConfigureAwait(false);

        // A tweak can write several values; it counts as one item, applied only when all of them were written.
        return ParseItems(result)
            .GroupBy(item => item.Item, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(item => !item.Ok) ?? group.First())
            .ToList();
    }

    /// <exception cref="GuestOperationException">The result does not have the expected shape.</exception>
    internal static IReadOnlyList<ApplyItemResult> ParseItems(JsonNode? result)
    {
        var items = result switch
        {
            JsonObject found when found["items"] is JsonArray array => array,
            JsonObject found when found["items"] is JsonObject single => [single.DeepClone()],
            JsonObject found when found["items"] is null => [],
            _ => throw new GuestOperationException("The guest's setup results did not have the expected shape."),
        };
        return items.OfType<JsonObject>()
            .Where(item => (string?)item["item"] is { Length: > 0 })
            .Select(item => new ApplyItemResult(
                (string)item["item"]!,
                (bool?)item["ok"] ?? false,
                (string?)item["error"] is { Length: > 0 } error ? error.Trim() : null,
                (bool?)item["restart"] ?? false))
            .ToList();
    }

    private const string Connect = """
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

        """;

    private const string Finish = """

            Reply @{ ok = $true; result = $result }
        }
        catch {
            Reply @{ ok = $false; stage = 'guest'; error = $_.Exception.Message }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;

    /// <summary>
    /// Host-side script: winget installs, one at a time, with the winget.exe found in the DesktopAppInstaller package
    /// (the App Execution Alias is not there in a PowerShell Direct session). App Installer is registered for the
    /// administrator first: an account that never signed in interactively gets "Access is denied" from winget.exe
    /// otherwise (running it as SYSTEM fails too, with 0xC0000135). Winget packages are installed for the
    /// machine when they have such an installer, otherwise for the administrator. A first failure updates the sources
    /// once and tries again. Already installed counts as done.
    /// </summary>
    internal const string PackagesScript = Connect + """
        try {
            $result = Invoke-Command -Session $session -ArgumentList (,@($request.packages)) -ScriptBlock {
                param($packages)
                $items = @()
                $installer = Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1
                $winget = if ($installer) { Join-Path $installer.InstallLocation 'winget.exe' } else { $null }
                if (-not $winget -or -not (Test-Path $winget)) {
                    foreach ($package in $packages) { $items += @{ item = $package.item; ok = $false; error = 'winget (App Installer) is not installed in this VM.' } }
                    return @{ items = $items }
                }

                try { Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $installer.InstallLocation 'AppxManifest.xml') }
                catch {
                    $reason = 'App Installer could not be registered for the administrator: ' + $_.Exception.Message
                    foreach ($package in $packages) { $items += @{ item = $package.item; ok = $false; error = $reason } }
                    return @{ items = $items }
                }

                # winget writes progress and errors to its output and reports through its exit code, so a native
                # error must not end the script.
                $ErrorActionPreference = 'Continue'
                $noApplicableInstaller = -1978335216
                $done = @(0, -1978335189, -1978335135)
                $rebootToFinish = -1978334967
                $sourceDataMissing = -1978335217
                $sourcesRepaired = $false
                function Install($package, [bool]$machine) {
                    $arguments = @('install', '--id', $package.id, '--exact', '--source', $package.source, '--silent',
                        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
                    if ($machine) { $arguments += @('--scope', 'machine') }
                    $text = & $winget @arguments 2>&1 | Out-String
                    @{ code = $LASTEXITCODE; text = $text }
                }
                function Summary($text) {
                    $lines = @($text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '[A-Za-z]{3}' -and $_ -notmatch '^[\s\-\\|/]*$' })
                    $summary = ($lines | Select-Object -Last 2) -join ' '
                    if ($summary.Length -gt 300) { $summary = $summary.Substring(0, 300) }
                    $summary
                }

                function Attempt($package) {
                    $machine = $package.source -eq 'winget'
                    $run = Install $package $machine
                    if ($machine -and $run.code -eq $noApplicableInstaller) { $run = Install $package $false }
                    $run
                }

                foreach ($package in $packages) {
                    $run = Attempt $package
                    if ($done -notcontains $run.code -and $run.code -ne $rebootToFinish -and -not $sourcesRepaired) {
                        # A fresh Windows image has no source data for an account that never signed in
                        # (0x8A15000F); a reset rebuilds it, and the source package from Microsoft's CDN fixes the rest.
                        $sourcesRepaired = $true
                        & $winget source reset --force --disable-interactivity 2>&1 | Out-Null
                        & $winget source update --disable-interactivity 2>&1 | Out-Null
                        $run = Attempt $package
                        if ($run.code -eq $sourceDataMissing) {
                            try {
                                $msix = Join-Path $env:TEMP 'hyperharbor-winget-source.msix'
                                Invoke-WebRequest -Uri 'https://cdn.winget.microsoft.com/cache/source.msix' -OutFile $msix -UseBasicParsing -ErrorAction Stop
                                Add-AppxPackage -Path $msix -ErrorAction Stop
                                Remove-Item $msix -Force -ErrorAction SilentlyContinue
                            }
                            catch { }
                            $run = Attempt $package
                        }
                    }

                    if ($done -contains $run.code) { $items += @{ item = $package.item; ok = $true } }
                    elseif ($run.code -eq $rebootToFinish) { $items += @{ item = $package.item; ok = $true; restart = $true } }
                    else {
                        $code = '0x{0:X8}' -f $run.code
                        $items += @{ item = $package.item; ok = $false; error = ("winget install failed ($code). " + (Summary $run.text)).Trim() }
                    }
                }
                @{ items = $items }
            }
        """ + Finish;

    /// <summary>
    /// Host-side script: removes provisioned apps (and their installed copies for every account), Windows
    /// capabilities, and optional features, reporting when Windows needs a restart to finish. Something already
    /// absent counts as done; a name this Windows does not know is a problem.
    /// </summary>
    internal const string RemoveScript = Connect + """
        try {
            $result = Invoke-Command -Session $session -ArgumentList @($request.appx), @($request.capabilities), @($request.features) -ScriptBlock {
                param($appx, $capabilities, $features)
                $items = @()
                $provisioned = @(Get-AppxProvisionedPackage -Online)
                foreach ($app in $appx) {
                    try {
                        foreach ($package in @($provisioned | Where-Object { $_.DisplayName -eq $app.id })) {
                            Remove-AppxProvisionedPackage -Online -PackageName $package.PackageName | Out-Null
                        }
                        foreach ($package in @(Get-AppxPackage -AllUsers -Name $app.id)) {
                            Remove-AppxPackage -Package $package.PackageFullName -AllUsers
                        }
                        $items += @{ item = $app.item; ok = $true }
                    }
                    catch { $items += @{ item = $app.item; ok = $false; error = $_.Exception.Message } }
                }

                foreach ($capability in $capabilities) {
                    try {
                        $found = Get-WindowsCapability -Online -Name $capability.id
                        if (-not $found) { $items += @{ item = $capability.item; ok = $false; error = 'This Windows does not have that capability.' }; continue }
                        $restart = $false
                        if ($found.State -eq 'Installed') { $restart = [bool](Remove-WindowsCapability -Online -Name $capability.id).RestartNeeded }
                        $items += @{ item = $capability.item; ok = $true; restart = $restart }
                    }
                    catch { $items += @{ item = $capability.item; ok = $false; error = $_.Exception.Message } }
                }

                foreach ($feature in $features) {
                    try {
                        $found = Get-WindowsOptionalFeature -Online -FeatureName $feature.id
                        if (-not $found) { $items += @{ item = $feature.item; ok = $false; error = 'This Windows does not have that feature.' }; continue }
                        $restart = $false
                        if ([string]$found.State -like 'Enabled*') { $restart = [bool](Disable-WindowsOptionalFeature -Online -FeatureName $feature.id -NoRestart).RestartNeeded }
                        $items += @{ item = $feature.item; ok = $true; restart = $restart }
                    }
                    catch { $items += @{ item = $feature.item; ok = $false; error = $_.Exception.Message } }
                }
                @{ items = $items }
            }
        """ + Finish;

    /// <summary>
    /// Host-side script: writes registry values to HKLM and to the Default user's hive (C:\Users\Default\NTUSER.DAT,
    /// loaded under a temporary key and unloaded again), with .NET keys closed right away so the hive can unload.
    /// </summary>
    internal const string SettingsScript = Connect + """
        try {
            $result = Invoke-Command -Session $session -ArgumentList (,@($request.writes)) -ScriptBlock {
                param($writes)
                $items = @()
                $hive = 'HyperHarborDefault'
                $loaded = $false
                $loadError = $null
                if (@($writes | Where-Object { $_.target -eq 'default' }).Count -gt 0) {
                    # reg.exe writes its errors to stderr, which must not end the script, so its exit code is checked.
                    $ErrorActionPreference = 'Continue'
                    $output = & reg.exe load "HKU\$hive" (Join-Path $env:SystemDrive 'Users\Default\NTUSER.DAT') 2>&1 | Out-String
                    $code = $LASTEXITCODE
                    $ErrorActionPreference = 'Stop'
                    if ($code -eq 0) { $loaded = $true } else { $loadError = 'The Default user profile could not be loaded: ' + $output.Trim() }
                }

                try {
                    foreach ($write in $writes) {
                        try {
                            if ($write.target -eq 'default') {
                                if (-not $loaded) { throw $loadError }
                                $root = [Microsoft.Win32.Registry]::Users
                                $path = $hive + '\' + $write.key
                            }
                            else {
                                $root = [Microsoft.Win32.Registry]::LocalMachine
                                $path = $write.key
                            }

                            $text = [string]$write.value
                            $hex = $text -match '^0x[0-9a-fA-F]+$'
                            $key = $root.CreateSubKey($path)
                            try {
                                switch ($write.type) {
                                    'dword' {
                                        $number = if ($hex) { [Convert]::ToUInt32($text.Substring(2), 16) } elseif ($text.StartsWith('-')) { [uint32][BitConverter]::ToUInt32([BitConverter]::GetBytes([int32]$text), 0) } else { [uint32]$text }
                                        $key.SetValue($write.name, [BitConverter]::ToInt32([BitConverter]::GetBytes($number), 0), [Microsoft.Win32.RegistryValueKind]::DWord)
                                    }
                                    'qword' {
                                        $number = if ($hex) { [Convert]::ToUInt64($text.Substring(2), 16) } elseif ($text.StartsWith('-')) { [BitConverter]::ToUInt64([BitConverter]::GetBytes([int64]$text), 0) } else { [uint64]$text }
                                        $key.SetValue($write.name, [BitConverter]::ToInt64([BitConverter]::GetBytes($number), 0), [Microsoft.Win32.RegistryValueKind]::QWord)
                                    }
                                    default { $key.SetValue($write.name, $text, [Microsoft.Win32.RegistryValueKind]::String) }
                                }
                            }
                            finally { $key.Close() }
                            $items += @{ item = $write.item; ok = $true }
                        }
                        catch { $items += @{ item = $write.item; ok = $false; error = $_.Exception.Message } }
                    }
                }
                finally {
                    if ($loaded) {
                        # The hive must not stay loaded: new profiles copy C:\Users\Default only when it is free.
                        $ErrorActionPreference = 'Continue'
                        $unloaded = $false
                        for ($attempt = 0; $attempt -lt 5 -and -not $unloaded; $attempt++) {
                            [GC]::Collect()
                            [GC]::WaitForPendingFinalizers()
                            & reg.exe unload "HKU\$hive" 2>&1 | Out-Null
                            $unloaded = $LASTEXITCODE -eq 0
                            if (-not $unloaded) { Start-Sleep -Seconds 1 }
                        }
                        if (-not $unloaded) { $items += @{ item = 'Default user settings'; ok = $false; error = 'The Default user profile stayed loaded; restart the VM before signing in for the first time.' } }
                    }
                }
                @{ items = $items }
            }
        """ + Finish;
}
