namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>
/// The Windows PowerShell scripts of the Lean host action, one per job so each stays far under the command line
/// limit. They run as the host service (LocalSystem) or an elevated process. Rules every script follows: read the
/// current state before writing it, treat "already as wanted" as done, report per item and never stop at the first
/// failure, and never run a command line that a non-administrator could have written (per-user uninstallers run as
/// that user through a scheduled task, not as SYSTEM).
/// </summary>
internal static class HostScripts
{
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 8 }
        $lm = [Microsoft.Win32.Registry]::LocalMachine
        $us = [Microsoft.Win32.Registry]::Users
        $cv = 'SOFTWARE\Microsoft\Windows\CurrentVersion'
        $script:loaded = @()
        $script:unloadFailed = @()

        # Signed-in users' hives are loaded under HKU\<SID>.
        function UserSids { @($us.GetSubKeyNames() | Where-Object { $_ -match '^S-1-5-21-[0-9-]+$' }) }

        # reg.exe writes its errors to stderr, which must not end the script, so its exit code is checked.
        function Native($file, $arguments) {
            $previous = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            $text = ((& $file @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n").Trim()
            $code = $LASTEXITCODE
            $ErrorActionPreference = $previous
            @{ code = $code; text = $text }
        }

        function LoadHive($name, $file) {
            $run = Native 'reg.exe' @('load', "HKU\$name", $file)
            if ($run.code -ne 0) { throw ("$file could not be loaded: " + $run.text) }
            $script:loaded += $name
        }

        # A hive must not stay loaded: an account whose hive is loaded gets a temporary profile at its next sign-in.
        function UnloadHives {
            foreach ($name in $script:loaded) {
                $done = $false
                for ($attempt = 0; $attempt -lt 5 -and -not $done; $attempt++) {
                    [GC]::Collect()
                    [GC]::WaitForPendingFinalizers()
                    $done = (Native 'reg.exe' @('unload', "HKU\$name")).code -eq 0
                    if (-not $done) { Start-Sleep -Seconds 1 }
                }
                if (-not $done) { $script:unloadFailed += $name }
            }
        }

        """;

    private const string Finish = """

            Reply @{ ok = $true; result = $result }
        }
        catch {
            Reply @{ ok = $false; error = $_.Exception.Message }
        }
        """;

    /// <summary>Reads services, startup entries, power settings, apps, programs, printers, and the requested registry values.</summary>
    public const string Inspect = Prelude + """
        try {
            $sids = UserSids
            $warnings = @()

            $services = @(Get-CimInstance Win32_Service | ForEach-Object {
                $startup = switch ($_.StartMode) {
                    'Auto' { if ($_.DelayedAutoStart) { 'automaticDelayed' } else { 'automatic' } }
                    'Manual' { 'manual' }
                    'Disabled' { 'disabled' }
                    'Boot' { 'boot' }
                    'System' { 'system' }
                    default { 'manual' }
                }
                @{ name = $_.Name; display = [string]$_.DisplayName; startup = $startup; running = ($_.State -eq 'Running') }
            })

            # Startup entries. Task Manager marks them disabled with a first byte of 3 in StartupApproved.
            $startup = @()
            function Approved($root, $path, $name) {
                $key = $root.OpenSubKey($path)
                if ($null -eq $key) { return $true }
                try {
                    $bytes = $key.GetValue($name, $null)
                    if ($bytes -is [byte[]] -and $bytes.Length -gt 0) { return (($bytes[0] -band 1) -eq 0) }
                    return $true
                }
                finally { $key.Close() }
            }
            function AddRunKey($scope, $source, $root, $runPath, $approvedPath) {
                $key = $root.OpenSubKey($runPath)
                if ($null -eq $key) { return }
                try {
                    foreach ($name in $key.GetValueNames()) {
                        if ($name -eq '') { continue }
                        $script:startup += @{ scope = $scope; source = $source; name = $name; command = [string]$key.GetValue($name); enabled = (Approved $root $approvedPath $name) }
                    }
                }
                finally { $key.Close() }
            }
            function AddFolder($scope, $folder, $root, $approvedPath) {
                if (-not (Test-Path -LiteralPath $folder)) { return }
                foreach ($file in @(Get-ChildItem -LiteralPath $folder -File -Force | Where-Object { $_.Name -ne 'desktop.ini' })) {
                    $script:startup += @{ scope = $scope; source = 'Folder'; name = $file.Name; command = $file.FullName; enabled = (Approved $root $approvedPath $file.Name) }
                }
            }
            AddRunKey 'machine' 'Run' $lm "$cv\Run" "$cv\Explorer\StartupApproved\Run"
            AddRunKey 'machine' 'Run32' $lm 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' "$cv\Explorer\StartupApproved\Run32"
            AddFolder 'machine' (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup') $lm "$cv\Explorer\StartupApproved\StartupFolder"
            foreach ($sid in $sids) {
                AddRunKey "user:$sid" 'Run' $us "$sid\$cv\Run" "$sid\$cv\Explorer\StartupApproved\Run"
                $profilePath = (Get-ItemProperty -LiteralPath "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -ErrorAction SilentlyContinue).ProfileImagePath
                if ($profilePath) { AddFolder "user:$sid" (Join-Path $profilePath 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup') $us "$sid\$cv\Explorer\StartupApproved\StartupFolder" }
            }

            # Power: the plans, and which devices may wake the PC.
            $guid = '([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})'
            $activeText = (& powercfg.exe /getactivescheme | Out-String)
            $active = if ($activeText -match $guid) { $Matches[1].ToLowerInvariant() } else { '' }
            $plans = @()
            foreach ($line in @(& powercfg.exe /list)) { if ($line -match "$guid\s+\((.+?)\)") { $plans += @{ guid = $Matches[1].ToLowerInvariant(); name = $Matches[2] } } }
            function DeviceQuery($kind) { @(& powercfg.exe /devicequery $kind | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne 'NONE' }) }
            $armed = @(DeviceQuery 'wake_armed')
            $programmable = @(DeviceQuery 'wake_programmable')
            $nics = @()
            try { $nics = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.MediaType -eq '802.3' } | ForEach-Object { [string]$_.InterfaceDescription }) } catch { }
            $networkDevices = @(($programmable + $armed) | Sort-Object -Unique | Where-Object {
                $device = $_
                @($nics | Where-Object { $device -eq $_ -or $device.StartsWith($_ + ' ') -or $_.StartsWith($device + ' ') }).Count -gt 0
            })

            # Apps: provisioned ones (new accounts) and installed ones (existing accounts).
            $appx = @()
            $appx += @(Get-AppxProvisionedPackage -Online | ForEach-Object { [string]$_.DisplayName })
            $appx += @(Get-AppxPackage -AllUsers | ForEach-Object { [string]$_.Name })
            $appx = @($appx | Where-Object { $_ } | Sort-Object -Unique)

            # Programs in the Uninstall keys.
            $programs = @()
            function AddPrograms($scope, $root, $path) {
                $key = $root.OpenSubKey($path)
                if ($null -eq $key) { return }
                try {
                    foreach ($sub in $key.GetSubKeyNames()) {
                        $entry = $key.OpenSubKey($sub)
                        if ($null -eq $entry) { continue }
                        try {
                            $display = $entry.GetValue('DisplayName')
                            if (-not $display) { continue }
                            if ([int]($entry.GetValue('SystemComponent', 0)) -eq 1) { continue }
                            $script:programs += @{
                                name = [string]$display; scope = $scope; key = $sub
                                uninstall = [string]$entry.GetValue('UninstallString'); quiet = [string]$entry.GetValue('QuietUninstallString')
                                msi = ([int]($entry.GetValue('WindowsInstaller', 0)) -eq 1)
                            }
                        }
                        finally { $entry.Close() }
                    }
                }
                finally { $key.Close() }
            }
            AddPrograms 'machine' $lm "$cv\Uninstall"
            AddPrograms 'machine' $lm 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
            foreach ($sid in $sids) { AddPrograms "user:$sid" $us "$sid\$cv\Uninstall" }

            $printers = @(Get-CimInstance Win32_Printer | ForEach-Object { @{ name = [string]$_.Name; port = [string]$_.PortName } })

            # Registry values. Users means every signed-in hive, then the Default user hive.
            function ReadValue($root, $path, $name) {
                $key = $root.OpenSubKey($path)
                if ($null -eq $key) { return @{ type = $null; value = $null } }
                try {
                    $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    if ($null -eq $value) { return @{ type = $null; value = $null } }
                    switch ($key.GetValueKind($name)) {
                        'DWord' { return @{ type = 'dword'; value = [string][BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$value), 0) } }
                        'QWord' { return @{ type = 'qword'; value = [string][BitConverter]::ToUInt64([BitConverter]::GetBytes([long]$value), 0) } }
                        'String' { return @{ type = 'string'; value = [string]$value } }
                        default { return @{ type = 'other'; value = $null } }
                    }
                }
                finally { $key.Close() }
            }
            $registry = @{}
            $probes = @($request.probes | Where-Object { $null -ne $_ })
            $defaultLoaded = $false
            if (@($probes | Where-Object { $_.target -eq 'users' }).Count -gt 0) {
                try { LoadHive 'HyperHarborLeanDefault' (Join-Path $env:SystemDrive 'Users\Default\NTUSER.DAT'); $defaultLoaded = $true }
                catch { $warnings += 'The Default user profile could not be read: ' + $_.Exception.Message }
            }
            try {
                foreach ($probe in $probes) {
                    $hives = @()
                    if ($probe.target -eq 'machine') {
                        $found = ReadValue $lm $probe.key $probe.name
                        $hives += @{ hive = 'HKLM'; type = $found.type; value = $found.value }
                    }
                    else {
                        foreach ($sid in $sids) {
                            $found = ReadValue $us "$sid\$($probe.key)" $probe.name
                            $hives += @{ hive = $sid; type = $found.type; value = $found.value }
                        }
                        if ($defaultLoaded) {
                            $found = ReadValue $us "HyperHarborLeanDefault\$($probe.key)" $probe.name
                            $hives += @{ hive = 'Default'; type = $found.type; value = $found.value }
                        }
                    }
                    $registry[[string]$probe.id] = $hives
                }
            }
            finally { UnloadHives }
            foreach ($name in $script:unloadFailed) { $warnings += "The registry hive $name stayed loaded; restart the PC before the next sign-in." }

            $result = @{
                services = $services; startup = $startup; appx = $appx; programs = $programs; printers = $printers
                registry = $registry; warnings = $warnings
                power = @{ active = $active; plans = $plans; armed = $armed; network = $networkDevices }
            }
        """ + Finish;

    /// <summary>Services (startup type, stop), startup entries (StartupApproved), the power plan, and wake devices.</summary>
    public const string System = Prelude + """
        try {
            $items = @()
            $startTypes = @{ automaticDelayed = 'delayed-auto'; automatic = 'auto'; manual = 'demand'; disabled = 'disabled' }

            foreach ($change in @($request.services | Where-Object { $null -ne $_ })) {
                try {
                    $name = [string]$change.name
                    $run = Native 'sc.exe' @('config', $name, 'start=', $startTypes[[string]$change.startup])
                    if ($run.code -ne 0) { throw ('sc.exe config failed (' + $run.code + '): ' + $run.text) }
                    if ($change.stop) { try { Stop-Service -Name $name -Force -ErrorAction Stop } catch { } }
                    $items += @{ item = $change.item; ok = $true }
                }
                catch { $items += @{ item = $change.item; ok = $false; error = $_.Exception.Message } }
            }

            foreach ($change in @($request.startup | Where-Object { $null -ne $_ })) {
                try {
                    $approved = switch ([string]$change.source) { 'Run' { 'Run' } 'Run32' { 'Run32' } default { 'StartupFolder' } }
                    $path = "$cv\Explorer\StartupApproved\$approved"
                    if ($change.scope -eq 'machine') { $root = $lm; $sub = $path }
                    elseif ($change.scope -match '^user:(S-1-5-21-[0-9-]+)$') { $root = $us; $sub = $Matches[1] + '\' + $path }
                    else { throw 'Unknown startup scope.' }
                    $key = $root.CreateSubKey($sub)
                    try {
                        $bytes = New-Object byte[] 12
                        if ($change.enable) { $bytes[0] = 2 }
                        else {
                            $bytes[0] = 3
                            [Array]::Copy([BitConverter]::GetBytes([DateTime]::UtcNow.ToFileTimeUtc()), 0, $bytes, 4, 8)
                        }
                        $key.SetValue([string]$change.name, $bytes, [Microsoft.Win32.RegistryValueKind]::Binary)
                    }
                    finally { $key.Close() }
                    $items += @{ item = $change.item; ok = $true }
                }
                catch { $items += @{ item = $change.item; ok = $false; error = $_.Exception.Message } }
            }

            $power = $request.power
            if ($power) {
                if ($power.plan) {
                    try {
                        if ($power.duplicate) {
                            $run = Native 'powercfg.exe' @('/duplicatescheme', [string]$power.plan, [string]$power.plan)
                            if ($run.code -ne 0) { throw ('powercfg /duplicatescheme failed: ' + $run.text) }
                        }
                        $run = Native 'powercfg.exe' @('/setactive', [string]$power.plan)
                        if ($run.code -ne 0) { throw ('powercfg /setactive failed: ' + $run.text) }
                        $items += @{ item = 'Power plan'; ok = $true }
                    }
                    catch { $items += @{ item = 'Power plan'; ok = $false; error = $_.Exception.Message } }
                }
                foreach ($device in @($power.disarm | Where-Object { $null -ne $_ })) {
                    $run = Native 'powercfg.exe' @('/devicedisablewake', [string]$device)
                    if ($run.code -eq 0) { $items += @{ item = "Wake: $device"; ok = $true } } else { $items += @{ item = "Wake: $device"; ok = $false; error = $run.text } }
                }
                foreach ($device in @($power.arm | Where-Object { $null -ne $_ })) {
                    $run = Native 'powercfg.exe' @('/deviceenablewake', [string]$device)
                    if ($run.code -eq 0) { $items += @{ item = "Wake: $device"; ok = $true } } else { $items += @{ item = "Wake: $device"; ok = $false; error = $run.text } }
                }
            }
            $result = @{ items = $items }
        """ + Finish;

    /// <summary>Writes and deletes registry values in HKLM, every signed-in user's hive, and the Default user hive.</summary>
    public const string Registry = Prelude + """
        try {
            $items = @()
            $sids = UserSids

            function WriteValue($root, $path, $write) {
                if ($write.type -eq 'absent') {
                    $key = $root.OpenSubKey($path, $true)
                    if ($null -ne $key) { try { $key.DeleteValue([string]$write.name, $false) } finally { $key.Close() } }
                    return
                }
                $text = [string]$write.value
                $hex = $text -match '^0x[0-9a-fA-F]+$'
                $key = $root.CreateSubKey($path)
                try {
                    switch ($write.type) {
                        'dword' {
                            $number = if ($hex) { [Convert]::ToUInt32($text.Substring(2), 16) } else { [uint32]$text }
                            $key.SetValue([string]$write.name, [BitConverter]::ToInt32([BitConverter]::GetBytes($number), 0), [Microsoft.Win32.RegistryValueKind]::DWord)
                        }
                        'qword' {
                            $number = if ($hex) { [Convert]::ToUInt64($text.Substring(2), 16) } else { [uint64]$text }
                            $key.SetValue([string]$write.name, [BitConverter]::ToInt64([BitConverter]::GetBytes($number), 0), [Microsoft.Win32.RegistryValueKind]::QWord)
                        }
                        default { $key.SetValue([string]$write.name, $text, [Microsoft.Win32.RegistryValueKind]::String) }
                    }
                }
                finally { $key.Close() }
            }

            $defaultError = $null
            if (@($request.writes | Where-Object { $_.target -eq 'users' }).Count -gt 0) {
                try { LoadHive 'HyperHarborLeanDefault' (Join-Path $env:SystemDrive 'Users\Default\NTUSER.DAT') }
                catch { $defaultError = 'The Default user profile: ' + $_.Exception.Message }
            }
            try {
                foreach ($write in @($request.writes | Where-Object { $null -ne $_ })) {
                    try {
                        if ($write.target -eq 'users') {
                            if ($defaultError) { throw $defaultError }
                            WriteValue $us ('HyperHarborLeanDefault\' + $write.key) $write
                            foreach ($sid in $sids) { WriteValue $us ("$sid\" + $write.key) $write }
                        }
                        else { WriteValue $lm $write.key $write }
                        $items += @{ item = $write.item; ok = $true }
                    }
                    catch { $items += @{ item = $write.item; ok = $false; error = $_.Exception.Message } }
                }
            }
            finally { UnloadHives }
            foreach ($name in $script:unloadFailed) { $items += @{ item = 'Account settings'; ok = $false; error = "The registry hive $name stayed loaded; restart the PC before the next sign-in." } }
            $result = @{ items = $items }
        """ + Finish;

    /// <summary>Removes provisioned apps and the installed copies for every account.</summary>
    public const string Appx = Prelude + """
        try {
            $items = @()
            $provisioned = @(Get-AppxProvisionedPackage -Online)
            foreach ($app in @($request.appx | Where-Object { $null -ne $_ })) {
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
            $result = @{ items = $items }
        """ + Finish;

    /// <summary>
    /// Uninstalls programs with their quiet uninstall command. A machine-wide program (HKLM, writable only by
    /// administrators) runs as this service; a per-user one runs as that user through a scheduled task, because its
    /// registry key is writable by that user and must never be run with more rights than the user has.
    /// </summary>
    public const string Programs = Prelude + """
        try {
            $items = @()

            function SplitCommand($command) {
                $text = $command.Trim()
                if ($text.StartsWith('"')) {
                    $end = $text.IndexOf('"', 1)
                    if ($end -gt 0) { return @($text.Substring(1, $end - 1), $text.Substring($end + 1).Trim()) }
                }
                $parts = @($text -split ' ')
                for ($count = $parts.Length; $count -ge 1; $count--) {
                    $candidate = ($parts[0..($count - 1)] -join ' ')
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return @($candidate, (($parts | Select-Object -Skip $count) -join ' ')) }
                }
                return @($parts[0], (($parts | Select-Object -Skip 1) -join ' '))
            }

            function RunHere($file, $arguments) {
                $info = @{ FilePath = $file; PassThru = $true; WindowStyle = 'Hidden' }
                if ($arguments) { $info.ArgumentList = $arguments }
                $process = Start-Process @info
                if (-not $process.WaitForExit(600000)) { try { $process.Kill() } catch { }; throw 'The uninstaller did not finish in 10 minutes.' }
                [int]$process.ExitCode
            }

            function RunAsUser($sid, $file, $arguments) {
                $account = (New-Object Security.Principal.SecurityIdentifier($sid)).Translate([Security.Principal.NTAccount]).Value
                $taskName = 'HyperHarbor Lean uninstall ' + [guid]::NewGuid().ToString('N')
                $action = if ($arguments) { New-ScheduledTaskAction -Execute $file -Argument $arguments } else { New-ScheduledTaskAction -Execute $file }
                $principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
                $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
                Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
                try {
                    Start-ScheduledTask -TaskName $taskName
                    $deadline = (Get-Date).AddMinutes(10)
                    do {
                        Start-Sleep -Seconds 2
                        $state = (Get-ScheduledTask -TaskName $taskName).State
                        $result = [int](Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
                    } while (($state -eq 'Running' -or $result -eq 267011 -or $result -eq 267009) -and (Get-Date) -lt $deadline)
                    return $result
                }
                finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
            }

            foreach ($program in @($request.programs | Where-Object { $null -ne $_ })) {
                try {
                    foreach ($name in @($program.processes | Where-Object { $null -ne $_ })) { Stop-Process -Name $name -Force -ErrorAction SilentlyContinue }

                    $file = $null
                    $arguments = $null
                    $command = if ($program.kind -eq 'oneDrive' -and $program.uninstall) { [string]$program.uninstall } elseif ($program.quiet) { [string]$program.quiet } else { $null }
                    if ($command) { $file, $arguments = SplitCommand $command }
                    elseif ($program.msi -or ($program.uninstall -match 'msiexec' -and $program.key -match '^\{[0-9A-Fa-f-]{36}\}$')) {
                        if ($program.key -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { throw 'The Windows Installer product code is missing.' }
                        $file = Join-Path $env:SystemRoot 'System32\msiexec.exe'
                        $arguments = '/x ' + $program.key + ' /qn /norestart'
                    }
                    else { throw 'It has no quiet uninstaller. Uninstall it from Settings > Apps.' }

                    if ($program.kind -eq 'oneDrive' -and $arguments -notmatch '/uninstall') { $arguments = ($arguments + ' /uninstall').Trim() }
                    $code = if ($program.scope -eq 'machine') { RunHere $file $arguments }
                    elseif ($program.scope -match '^user:(S-1-5-21-[0-9-]+)$') { RunAsUser $Matches[1] $file $arguments }
                    else { throw 'Unknown install scope.' }

                    if ($code -eq 0 -or $code -eq 1605) { $items += @{ item = $program.item; ok = $true } }
                    elseif ($code -eq 3010 -or $code -eq 1641) { $items += @{ item = $program.item; ok = $true; restart = $true } }
                    else { $items += @{ item = $program.item; ok = $false; error = "The uninstaller exited with code $code." } }
                }
                catch { $items += @{ item = $program.item; ok = $false; error = $_.Exception.Message } }
            }
            $result = @{ items = $items }
        """ + Finish;

    /// <summary>Makes a system restore point, lifting Windows' once-a-day limit for the call.</summary>
    public const string RestorePoint = Prelude + """
        try {
            $path = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $name = 'SystemRestorePointCreationFrequency'
            $previous = (Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue).$name
            Set-ItemProperty -Path $path -Name $name -Value 0 -Type DWord
            $started = Get-Date
            try { Checkpoint-Computer -Description ([string]$request.description) -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningAction SilentlyContinue }
            finally {
                if ($null -eq $previous) { Remove-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue }
                else { Set-ItemProperty -Path $path -Name $name -Value $previous -Type DWord }
            }
            $point = Get-ComputerRestorePoint | Sort-Object SequenceNumber -Descending | Select-Object -First 1
            $created = if ($point) { [Management.ManagementDateTimeConverter]::ToDateTime($point.CreationTime) } else { $null }
            if (-not $point -or $point.Description -ne [string]$request.description -or $created -lt $started.AddMinutes(-1)) {
                throw 'Windows did not make a restore point. Turn on System Protection for the system drive (System Properties, System Protection), then try again.'
            }
            $result = @{ sequence = [int]$point.SequenceNumber; description = [string]$point.Description }
        """ + Finish;

    /// <summary>Exports registry keys to .reg files; a key that does not exist is skipped.</summary>
    public const string ExportRegistry = Prelude + """
        try {
            New-Item -ItemType Directory -Path ([string]$request.folder) -Force | Out-Null
            $written = @()
            $index = 0
            foreach ($key in @($request.keys | Where-Object { $null -ne $_ })) {
                if ([string]$key -notmatch '^(HKLM|HKU)\\') { continue }
                if (-not (Test-Path -LiteralPath ('Registry::' + $key))) { continue }
                $index++
                $file = Join-Path ([string]$request.folder) ('export-{0:D3}.reg' -f $index)
                $run = Native 'reg.exe' @('export', [string]$key, $file, '/y')
                if ($run.code -ne 0) { throw ("reg export of $key failed: " + $run.text) }
                $written += $file
                Add-Content -LiteralPath (Join-Path ([string]$request.folder) 'index.txt') -Value ((Split-Path $file -Leaf) + "`t" + $key) -Encoding UTF8
            }
            $result = @{ files = $written }
        """ + Finish;
}
