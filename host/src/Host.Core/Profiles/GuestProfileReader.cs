using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>A provisioned package as the guest reports it.</summary>
public sealed record GuestAppxPackage(string Name, string Version, string PublisherId);

/// <summary>The guest's Windows build and edition, and its provisioned packages.</summary>
/// <param name="CurrentBuild">The major build, for example 26100.</param>
/// <param name="Ubr">The update build revision, for example 2033.</param>
/// <param name="Edition">EditionID, for example Professional.</param>
public sealed record GuestAppxInventory(string CurrentBuild, int Ubr, string Edition, IReadOnlyList<GuestAppxPackage> Packages)
{
    public string FullBuild => $"10.0.{CurrentBuild}.{Ubr}";
}

/// <summary>What is installed in a guest: winget's export when it works there, and the installed programs list.</summary>
/// <param name="WingetPackages">winget ids from winget export; null when winget could not run (see <paramref name="WingetError"/>).</param>
/// <param name="Programs">Display names from the Uninstall keys (machine and signed-in users), without system components.</param>
public sealed record GuestInstalledSoftware(IReadOnlyList<string>? WingetPackages, string? WingetError, IReadOnlyList<string> Programs);

/// <summary>A browser extension found in a guest's browser profile.</summary>
/// <param name="Browser">The browser catalog id: chrome, edge, or brave.</param>
public sealed record GuestExtension(string Browser, string ProfileId, string Name);

/// <summary>A registry value to read in the guest; HKCU means the User's account there.</summary>
public sealed record GuestRegistryRead(string Key, string Name);

/// <summary>Extensions in the User's browser profiles and the current registry values asked for.</summary>
/// <param name="ProfileFound">The User's own profile folder was found; otherwise every non-system profile was read.</param>
/// <param name="Values">Current values by "key|name"; null when absent.</param>
/// <param name="SettingsError">Why the account's own (HKCU) values could not be read, when they could not.</param>
public sealed record GuestBrowsersAndSettings(bool ProfileFound, IReadOnlyList<GuestExtension> Extensions, IReadOnlyDictionary<string, string?> Values, string? SettingsError = null);

/// <summary>Reads what is set up in a running Windows guest, for setup profiles. Read-only.</summary>
public interface IGuestProfileReader
{
    Task<GuestAppxInventory> ReadAppxAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken);

    Task<GuestInstalledSoftware> ReadInstalledAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken);

    /// <param name="userAccount">The User's account in the VM (for example hh-owner), whose profile and HKCU are read.</param>
    /// <param name="browsers">Browser id and User Data folder under %LOCALAPPDATA%.</param>
    Task<GuestBrowsersAndSettings> ReadBrowsersAndSettingsAsync(
        Guid vmId,
        GuestCredential admin,
        string userAccount,
        IReadOnlyList<(string Id, string UserData)> browsers,
        IReadOnlyList<GuestRegistryRead> values,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IGuestProfileReader"/> over PowerShell Direct, as the VM's stored administrator. Each read has its
/// own script, to stay under the command line limit (see <see cref="PowerShellDirectRunner"/>).
/// </summary>
public sealed class PowerShellDirectProfileReader : IGuestProfileReader
{
    private static readonly TimeSpan AppxTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InstalledTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BrowsersTimeout = TimeSpan.FromMinutes(3);
    private static readonly string EncodedAppxScript = PowerShellDirectRunner.Encode(AppxScript);
    private static readonly string EncodedInstalledScript = PowerShellDirectRunner.Encode(InstalledScript);
    private static readonly string EncodedBrowsersScript = PowerShellDirectRunner.Encode(BrowsersScript);

    public async Task<GuestAppxInventory> ReadAppxAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken)
    {
        var result = await PowerShellDirectRunner.RunAsync(EncodedAppxScript, new { vmId, admin.UserName, admin.Password }, AppxTimeout, cancellationToken).ConfigureAwait(false);
        return ParseAppx(result);
    }

    public async Task<GuestInstalledSoftware> ReadInstalledAsync(Guid vmId, GuestCredential admin, CancellationToken cancellationToken)
    {
        var result = await PowerShellDirectRunner.RunAsync(EncodedInstalledScript, new { vmId, admin.UserName, admin.Password }, InstalledTimeout, cancellationToken).ConfigureAwait(false);
        return ParseInstalled(result);
    }

    public async Task<GuestBrowsersAndSettings> ReadBrowsersAndSettingsAsync(
        Guid vmId,
        GuestCredential admin,
        string userAccount,
        IReadOnlyList<(string Id, string UserData)> browsers,
        IReadOnlyList<GuestRegistryRead> values,
        CancellationToken cancellationToken)
    {
        var result = await PowerShellDirectRunner.RunAsync(EncodedBrowsersScript, new
        {
            vmId,
            admin.UserName,
            admin.Password,
            account = userAccount,
            browsers = browsers.Select(browser => new { id = browser.Id, userData = browser.UserData }),
            values = values.Select(value => new { key = value.Key, name = value.Name }),
        }, BrowsersTimeout, cancellationToken).ConfigureAwait(false);
        return ParseBrowsers(result);
    }

    internal static GuestInstalledSoftware ParseInstalled(JsonNode? result)
    {
        if (result is not JsonObject installed)
        {
            throw new GuestOperationException("The guest's installed software list did not have the expected shape.");
        }

        var winget = installed["winget"] is JsonArray ids
            ? ids.Select(id => (string?)id).OfType<string>().Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : installed["winget"] is JsonValue single && (string?)single is { Length: > 0 } one ? [one] : null;
        return new GuestInstalledSoftware(
            winget,
            (string?)installed["wingetError"],
            Strings(installed["programs"]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    internal static GuestBrowsersAndSettings ParseBrowsers(JsonNode? result)
    {
        if (result is not JsonObject found)
        {
            throw new GuestOperationException("The guest's browser and settings list did not have the expected shape.");
        }

        var extensions = AsArray(found["extensions"]).OfType<JsonObject>()
            .Select(item => (Browser: (string?)item["browser"], Id: (string?)item["id"], Store: (string?)item["store"], Name: (string?)item["name"]))
            .Where(item => item.Browser is not null && item.Id is not null)
            .Select(item => ExtensionIdParser.Parse(item.Store == "edge" ? ExtensionRef.EdgePrefix + item.Id : item.Id) is { } parsed
                ? new GuestExtension(item.Browser!, parsed.ProfileId, string.IsNullOrWhiteSpace(item.Name) ? parsed.Id : item.Name!.Trim())
                : null)
            .OfType<GuestExtension>()
            .DistinctBy(item => (item.Browser, item.ProfileId))
            .ToList();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AsArray(found["values"]).OfType<JsonObject>())
        {
            if ((string?)item["key"] is { } key && (string?)item["name"] is { } name)
            {
                values[$"{key}|{name}"] = item["value"] is JsonValue value ? value.ToString() : null;
            }
        }

        return new GuestBrowsersAndSettings((bool?)found["profileFound"] ?? false, extensions, values, (string?)found["settingsError"]);
    }

    private static JsonArray AsArray(JsonNode? node) => node switch
    {
        JsonArray array => array,
        JsonObject single => [single.DeepClone()],
        _ => [],
    };

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(item => (string?)item).OfType<string>().Select(text => text.Trim()).Where(text => text.Length > 0),
        JsonValue single when (string?)single is { Length: > 0 } text => [text.Trim()],
        _ => [],
    };

    /// <exception cref="GuestOperationException">The result does not have the expected shape.</exception>
    internal static GuestAppxInventory ParseAppx(JsonNode? result)
    {
        if (result is not JsonObject inventory || (string?)inventory["build"] is not { Length: > 0 } build)
        {
            throw new GuestOperationException("The guest's package list did not have the expected shape.");
        }

        var packages = inventory["packages"] switch
        {
            JsonArray array => array,
            JsonObject single => [single.DeepClone()],
            _ => [],
        };
        return new GuestAppxInventory(
            build,
            (int?)inventory["ubr"] ?? 0,
            (string?)inventory["edition"] ?? "Unknown",
            packages.OfType<JsonObject>()
                .Where(package => (string?)package["name"] is { Length: > 0 })
                .Select(package => new GuestAppxPackage((string)package["name"]!, (string?)package["version"] ?? "", (string?)package["publisherId"] ?? ""))
                .ToList());
    }

    /// <summary>
    /// Host-side script: winget export, run as the VM's administrator with the winget.exe found in the
    /// DesktopAppInstaller package (the App Execution Alias is not there in a PowerShell Direct session), and the
    /// display names in the Uninstall keys, which also cover what winget does not know.
    /// </summary>
    internal const string InstalledScript = """
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
                $ids = $null
                $wingetError = $null
                try {
                    $installer = Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1
                    $winget = if ($installer) { Join-Path $installer.InstallLocation 'winget.exe' } else { $null }
                    if (-not $winget -or -not (Test-Path $winget)) { throw 'winget (App Installer) is not installed in this VM.' }
                    # An account that never signed in interactively gets "Access is denied" from winget.exe until
                    # App Installer is registered for it.
                    Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $installer.InstallLocation 'AppxManifest.xml')
                    $file = Join-Path $env:TEMP ('hyperharbor-export-' + [guid]::NewGuid() + '.json')
                    $output = & $winget export --output $file --accept-source-agreements --disable-interactivity 2>&1 | Out-String
                    if (-not (Test-Path $file)) { throw ("winget export wrote nothing (exit code $LASTEXITCODE). " + $output.Trim()) }
                    $export = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
                    Remove-Item $file -Force -ErrorAction SilentlyContinue
                    $ids = @($export.Sources | ForEach-Object { $_.Packages } | ForEach-Object { [string]$_.PackageIdentifier })
                }
                catch {
                    $wingetError = $_.Exception.Message
                }

                $keys = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
                Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^S-1-5-21-[\d-]+$' } | ForEach-Object {
                    $keys += "Registry::HKEY_USERS\$($_.PSChildName)\Software\Microsoft\Windows\CurrentVersion\Uninstall\*"
                }
                $programs = @(Get-ItemProperty $keys -ErrorAction SilentlyContinue |
                    Where-Object { $_.DisplayName -and -not $_.SystemComponent -and -not $_.ParentKeyName -and $_.ReleaseType -notmatch 'Update|Hotfix' } |
                    ForEach-Object { [string]$_.DisplayName })
                @{ winget = $ids; wingetError = $wingetError; programs = $programs }
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

    /// <summary>
    /// Host-side script: browser extensions and registry values of the User's account in the VM. The profile is
    /// found by the account's SID; when the account has none yet, every non-system profile is read. HKCU values come
    /// from the loaded hive, or from NTUSER.DAT loaded under a temporary key and unloaded again. Extensions without a
    /// store update URL (component or unpacked ones) are skipped; names given as __MSG_x__ are read from the
    /// extension's default locale.
    /// </summary>
    internal const string BrowsersScript = """
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
            $result = Invoke-Command -Session $session -ArgumentList $request.account, @($request.browsers), @($request.values) -ScriptBlock {
                param($account, $browsers, $values)
                $sid = $null
                try { $sid = (Get-LocalUser -Name $account -ErrorAction Stop).SID.Value } catch { }
                $profiles = @(Get-CimInstance Win32_UserProfile | Where-Object { -not $_.Special -and $_.LocalPath })
                $own = @($profiles | Where-Object { $sid -and $_.SID -eq $sid })
                $read = if ($own.Count -gt 0) { $own } else { $profiles }

                $extensions = @()
                foreach ($userProfile in $read) {
                    foreach ($browser in $browsers) {
                        $userData = Join-Path $userProfile.LocalPath ('AppData\Local\' + $browser.userData)
                        if (-not (Test-Path $userData)) { continue }
                        $folders = Get-ChildItem $userData -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' }
                        foreach ($folder in $folders) {
                            foreach ($extension in (Get-ChildItem (Join-Path $folder.FullName 'Extensions') -Directory -ErrorAction SilentlyContinue)) {
                                $version = Get-ChildItem $extension.FullName -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
                                if (-not $version) { continue }
                                try { $manifest = Get-Content (Join-Path $version.FullName 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
                                $update = [string]$manifest.update_url
                                $store = if ($update -match 'clients2\.google\.com') { 'chrome' } elseif ($update -match 'edge\.microsoft\.com') { 'edge' } else { $null }
                                if (-not $store) { continue }
                                $name = [string]$manifest.name
                                if ($name -match '^__MSG_(.+)__$') {
                                    $key = $Matches[1]
                                    $locale = if ($manifest.default_locale) { $manifest.default_locale } else { 'en' }
                                    try {
                                        $messages = Get-Content (Join-Path $version.FullName "_locales\$locale\messages.json") -Raw -Encoding UTF8 | ConvertFrom-Json
                                        $entry = $messages.PSObject.Properties | Where-Object { $_.Name -ieq $key } | Select-Object -First 1
                                        $name = if ($entry) { [string]$entry.Value.message } else { '' }
                                    }
                                    catch { $name = '' }
                                }
                                $extensions += @{ browser = $browser.id; id = $extension.Name; store = $store; name = $name }
                            }
                        }
                    }
                }

                $found = @()
                $hive = $null
                $loaded = $false
                $settingsError = $null
                if ($sid -and (Test-Path "Registry::HKEY_USERS\$sid")) {
                    $hive = $sid
                }
                elseif ($own.Count -gt 0) {
                    # Not signed in: load the account's hive under a temporary key. reg.exe writes its errors to
                    # stderr, which must not end the script, so the result is checked instead.
                    $file = Join-Path $own[0].LocalPath 'NTUSER.DAT'
                    $ErrorActionPreference = 'Continue'
                    $output = & reg.exe load 'HKU\HyperHarborCapture' $file 2>&1 | Out-String
                    $code = $LASTEXITCODE
                    $ErrorActionPreference = 'Stop'
                    if ($code -eq 0) { $hive = 'HyperHarborCapture'; $loaded = $true }
                    else { $settingsError = "reg load of $file failed: " + $output.Trim() }
                }
                try {
                    # .NET keys, each closed right away: an open handle would keep the loaded hive from unloading.
                    foreach ($value in $values) {
                        $root = $null
                        $path = $null
                        if ($value.key -like 'HKLM\*') { $root = [Microsoft.Win32.Registry]::LocalMachine; $path = $value.key.Substring(5) }
                        elseif ($hive) { $root = [Microsoft.Win32.Registry]::Users; $path = $hive + '\' + $value.key.Substring(5) }
                        $current = $null
                        if ($root) {
                            $key = $root.OpenSubKey($path)
                            if ($key) {
                                try { if ($key.GetValueNames() -contains $value.name) { $current = [string]$key.GetValue($value.name) } }
                                finally { $key.Close() }
                            }
                        }
                        $found += @{ key = $value.key; name = $value.name; value = $current }
                    }
                }
                finally {
                    if ($loaded) {
                        # The hive must not stay loaded: the account's next sign-in would get a temporary profile.
                        $ErrorActionPreference = 'Continue'
                        $unloaded = $false
                        for ($attempt = 0; $attempt -lt 5 -and -not $unloaded; $attempt++) {
                            [GC]::Collect()
                            [GC]::WaitForPendingFinalizers()
                            & reg.exe unload 'HKU\HyperHarborCapture' 2>&1 | Out-Null
                            $unloaded = $LASTEXITCODE -eq 0
                            if (-not $unloaded) { Start-Sleep -Seconds 1 }
                        }
                        $ErrorActionPreference = 'Stop'
                        if (-not $unloaded) { $settingsError = 'The account settings were read, but the temporary registry hive could not be unloaded; restart the VM before signing in as that account.' }
                    }
                }

                @{ profileFound = ($own.Count -gt 0); extensions = $extensions; values = $found; settingsError = $settingsError }
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

    /// <summary>
    /// Host-side script: provisioned packages (Get-AppxProvisionedPackage -Online) with the Windows build and
    /// edition. Output is UTF-8, because package and publisher names may not be ASCII.
    /// </summary>
    internal const string AppxScript = """
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
                $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
                $packages = @(Get-AppxProvisionedPackage -Online | ForEach-Object {
                    @{ name = [string]$_.DisplayName; version = [string]$_.Version; publisherId = [string]$_.PublisherId }
                })
                @{ build = [string]$version.CurrentBuild; ubr = [int]$version.UBR; edition = [string]$version.EditionID; packages = $packages }
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
