using System.Text.Json.Nodes;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// Manages guest accounts with PowerShell Direct (New-PSSession -VMId) through <see cref="PowerShellDirectRunner"/>.
/// Credentials and passwords travel on standard input as JSON and are converted to SecureString before
/// use, so they never appear on a command line, on disk, or in logs.
/// </summary>
public sealed class PowerShellDirectAccountManager : IGuestAccountManager
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task<GuestAccountState> InspectAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken)
    {
        var result = await RunAsync(new { operation = "inspect", vmId = target.VmId, admin.UserName, admin.Password, accountName }, cancellationToken);
        return new GuestAccountState(
            (bool?)result?["exists"] ?? false,
            (bool?)result?["isLocal"] ?? false,
            (bool?)result?["enabled"] ?? false,
            (bool?)result?["inRemoteDesktopUsers"] ?? false);
    }

    public async Task<GuestTarget> ProvisionAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string password,
        GuestProvisionOptions options,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            new { operation = "provision", vmId = target.VmId, admin.UserName, admin.Password, accountName, accountPassword = password, options.EnableRemoteDesktop },
            cancellationToken);
        return target;
    }

    public async Task SetPasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken)
    {
        await RunAsync(new { operation = "setPassword", vmId = target.VmId, admin.UserName, admin.Password, accountName, accountPassword = password }, cancellationToken);
    }

    public async Task<PasswordChange> ChangePasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string currentPassword, string password, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            new { operation = "changePassword", vmId = target.VmId, admin.UserName, admin.Password, accountName, currentPassword, accountPassword = password },
            cancellationToken);
        return (bool?)result?["changed"] == true
            ? PasswordChange.Success
            : new PasswordChange(false, (string?)result?["reason"] is { Length: > 0 } reason ? reason : "The guest did not say why.");
    }

    public async Task<GuestAdminAccountState> InspectAdminAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken)
    {
        var result = await RunAdminAsync(new { operation = "inspectAdmin", vmId = target.VmId, admin.UserName, admin.Password, accountName }, cancellationToken);
        return ParseAdminState(result);
    }

    public async Task<GuestAdminAccountState> EnableAdminAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string fullName,
        string password,
        CancellationToken cancellationToken)
    {
        var result = await RunAdminAsync(
            new { operation = "enableAdmin", vmId = target.VmId, admin.UserName, admin.Password, accountName, fullName, accountPassword = password },
            cancellationToken);
        return ParseAdminState(result);
    }

    public async Task LockAdminAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken)
    {
        await RunAdminAsync(new { operation = "lockAdmin", vmId = target.VmId, admin.UserName, admin.Password, accountName, accountPassword = password }, cancellationToken);
    }

    internal static GuestAdminAccountState ParseAdminState(JsonNode? result) => new(
        (bool?)result?["exists"] ?? false,
        (bool?)result?["enabled"] ?? false,
        (bool?)result?["inAdministrators"] ?? false,
        (bool?)result?["inRemoteDesktopUsers"] ?? false,
        (bool?)result?["deniedNetworkLogon"] ?? false,
        (bool?)result?["deniedRemoteInteractiveLogon"] ?? false);

    private static Task<JsonNode?> RunAsync(object payload, CancellationToken cancellationToken) =>
        PowerShellDirectRunner.RunAsync(EncodedScript, payload, Timeout, cancellationToken);

    private static Task<JsonNode?> RunAdminAsync(object payload, CancellationToken cancellationToken) =>
        PowerShellDirectRunner.RunAsync(EncodedAdminScript, payload, Timeout, cancellationToken);

    private static readonly string EncodedAdminScript = PowerShellDirectRunner.Encode(AdminScript);

    /// <summary>
    /// Host-side script for administrator windows; same request and reply shape as <see cref="Script"/>.
    /// Administrators (S-1-5-32-544) and Remote Desktop Users (S-1-5-32-555) are addressed by SID, so it works on
    /// localized guests; membership is read through ADSI, which also works when a group holds a SID that no longer
    /// resolves (Get-LocalGroupMember fails on such groups). Windows PowerShell has no cmdlet for logon rights, so
    /// the guest compiles a small LSA wrapper with Add-Type (LsaAddAccountRights adds to the rights the account
    /// already holds and leaves every other account's rights alone).
    /// </summary>
    internal const string AdminScript = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Secure([string]$text) { ConvertTo-SecureString $text -AsPlainText -Force }
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 4 }

        $lsa = @'
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Runtime.InteropServices;
        using System.Security.Principal;

        public static class HyperHarborAccountRights
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

            [StructLayout(LayoutKind.Sequential)]
            private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public int Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

            private const int PolicyCreateAccount = 0x10;
            private const int PolicyLookupNames = 0x800;
            private const uint ObjectNameNotFound = 0xC0000034;

            [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(IntPtr systemName, ref ObjectAttributes attributes, int access, out IntPtr handle);
            [DllImport("advapi32.dll")] private static extern uint LsaAddAccountRights(IntPtr handle, byte[] sid, UnicodeString[] rights, int count);
            [DllImport("advapi32.dll")] private static extern uint LsaEnumerateAccountRights(IntPtr handle, byte[] sid, out IntPtr rights, out int count);
            [DllImport("advapi32.dll")] private static extern uint LsaFreeMemory(IntPtr buffer);
            [DllImport("advapi32.dll")] private static extern uint LsaClose(IntPtr handle);
            [DllImport("advapi32.dll")] private static extern int LsaNtStatusToWinError(uint status);

            public static void Add(string sid, string[] rights)
            {
                IntPtr policy = Open(PolicyLookupNames | PolicyCreateAccount);
                UnicodeString[] names = new UnicodeString[rights.Length];
                try
                {
                    for (int i = 0; i < rights.Length; i++)
                    {
                        names[i].Buffer = Marshal.StringToHGlobalUni(rights[i]);
                        names[i].Length = (ushort)(rights[i].Length * 2);
                        names[i].MaximumLength = (ushort)(names[i].Length + 2);
                    }
                    Check(LsaAddAccountRights(policy, Binary(sid), names, names.Length));
                }
                finally
                {
                    foreach (UnicodeString name in names) { if (name.Buffer != IntPtr.Zero) { Marshal.FreeHGlobal(name.Buffer); } }
                    LsaClose(policy);
                }
            }

            public static string[] List(string sid)
            {
                IntPtr policy = Open(PolicyLookupNames);
                try
                {
                    IntPtr buffer;
                    int count;
                    uint status = LsaEnumerateAccountRights(policy, Binary(sid), out buffer, out count);
                    if (status == ObjectNameNotFound) { return new string[0]; }
                    Check(status);
                    try
                    {
                        List<string> result = new List<string>();
                        int size = Marshal.SizeOf(typeof(UnicodeString));
                        for (int i = 0; i < count; i++)
                        {
                            UnicodeString name = (UnicodeString)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + i * size), typeof(UnicodeString));
                            result.Add(Marshal.PtrToStringUni(name.Buffer, name.Length / 2));
                        }
                        return result.ToArray();
                    }
                    finally { LsaFreeMemory(buffer); }
                }
                finally { LsaClose(policy); }
            }

            private static IntPtr Open(int access)
            {
                ObjectAttributes attributes = new ObjectAttributes();
                IntPtr handle;
                Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, access, out handle));
                return handle;
            }

            private static byte[] Binary(string sid)
            {
                SecurityIdentifier identifier = new SecurityIdentifier(sid);
                byte[] bytes = new byte[identifier.BinaryLength];
                identifier.GetBinaryForm(bytes, 0);
                return bytes;
            }

            private static void Check(uint status)
            {
                if (status != 0) { throw new Win32Exception(LsaNtStatusToWinError(status)); }
            }
        }
        '@

        try {
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, (Secure $request.Password))
            $session = New-PSSession -VMId $request.vmId -Credential $credential
        }
        catch {
            Reply @{ ok = $false; stage = 'connect'; error = $_.Exception.Message }
            exit 0
        }

        $inspect = {
            param($name, $lsa)
            $ErrorActionPreference = 'Stop'
            # Compares SIDs: ADSI's IsMember compares paths, and a member's path includes the workgroup.
            function IsMember($groupSid, $memberSid) {
                $group = (New-Object System.Security.Principal.SecurityIdentifier $groupSid).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
                $sids = @(([ADSI]"WinNT://$env:COMPUTERNAME/$group,group").Invoke('Members') | ForEach-Object {
                    (New-Object System.Security.Principal.SecurityIdentifier ([byte[]]$_.GetType().InvokeMember('objectSid', 'GetProperty', $null, $_, $null)), 0).Value
                })
                $sids -contains $memberSid
            }
            $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if (-not $user) {
                return @{ exists = $false; enabled = $false; inAdministrators = $false; inRemoteDesktopUsers = $false; deniedNetworkLogon = $false; deniedRemoteInteractiveLogon = $false }
            }
            Add-Type -TypeDefinition $lsa
            $rights = @([HyperHarborAccountRights]::List($user.SID.Value))
            @{
                exists = $true
                enabled = [bool]$user.Enabled
                inAdministrators = (IsMember 'S-1-5-32-544' $user.SID.Value)
                inRemoteDesktopUsers = (IsMember 'S-1-5-32-555' $user.SID.Value)
                deniedNetworkLogon = ($rights -contains 'SeDenyNetworkLogonRight')
                deniedRemoteInteractiveLogon = ($rights -contains 'SeDenyRemoteInteractiveLogonRight')
            }
        }

        $enable = {
            param($name, $fullName, [securestring]$password, $lsa)
            $ErrorActionPreference = 'Stop'
            $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($user) {
                if ([string]$user.PrincipalSource -ne 'Local') { throw "NOT_LOCAL:An account named $name exists but is not a local account." }
            }
            else {
                $user = New-LocalUser -Name $name -Password $password -FullName $fullName -Description 'HyperHarbor administrator window account' `
                    -PasswordNeverExpires -AccountNeverExpires -UserMayNotChangePassword -Disabled
            }
            Set-LocalUser -Name $name -Password $password -FullName $fullName -PasswordNeverExpires $true
            try { Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $user } catch { if ($_.FullyQualifiedErrorId -notlike 'MemberExists*') { throw } }
            try { Remove-LocalGroupMember -SID 'S-1-5-32-555' -Member $user } catch { if ($_.FullyQualifiedErrorId -notlike 'MemberNotFound*') { throw } }
            Add-Type -TypeDefinition $lsa
            [HyperHarborAccountRights]::Add($user.SID.Value, @('SeDenyNetworkLogonRight', 'SeDenyRemoteInteractiveLogonRight'))
            Enable-LocalUser -Name $name
        }

        # Ends a window: a new random password, and the account stays enabled so UAC keeps offering it.
        $lock = {
            param($name, [securestring]$password)
            $ErrorActionPreference = 'Stop'
            if (-not (Get-LocalUser -Name $name -ErrorAction SilentlyContinue)) { return }
            Set-LocalUser -Name $name -Password $password
            Enable-LocalUser -Name $name
        }

        try {
            switch ($request.operation) {
                'inspectAdmin' { $result = Invoke-Command -Session $session -ScriptBlock $inspect -ArgumentList $request.accountName, $lsa }
                'enableAdmin' {
                    Invoke-Command -Session $session -ScriptBlock $enable -ArgumentList $request.accountName, $request.fullName, (Secure $request.accountPassword), $lsa
                    $result = Invoke-Command -Session $session -ScriptBlock $inspect -ArgumentList $request.accountName, $lsa
                }
                'lockAdmin' {
                    Invoke-Command -Session $session -ScriptBlock $lock -ArgumentList $request.accountName, (Secure $request.accountPassword)
                    $result = $null
                }
                default { throw "Unknown operation $($request.operation)." }
            }
            Reply @{ ok = $true; result = $result }
        }
        catch {
            $message = $_.Exception.Message
            if ($message.StartsWith('NOT_LOCAL:')) { Reply @{ ok = $false; stage = 'notLocal'; error = $message.Substring(10) } }
            else { Reply @{ ok = $false; stage = 'guest'; error = $message } }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;

    /// <summary>Maps the script's JSON result to a value or a typed exception.</summary>
    internal static JsonNode? Interpret(string output, string errors) => PowerShellDirectRunner.Interpret(output, errors);

    private static readonly string EncodedScript = PowerShellDirectRunner.Encode(Script);

    /// <summary>
    /// Host-side script. Reads one JSON request from stdin and writes one JSON line:
    /// { ok: true, result } or { ok: false, stage: connect|notLocal|guest, error }.
    /// Remote Desktop Users is addressed by SID S-1-5-32-555 and the firewall rule group by its
    /// resource string, so the script works on localized guests.
    /// </summary>
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Secure([string]$text) { ConvertTo-SecureString $text -AsPlainText -Force }
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 4 }

        try {
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, (Secure $request.Password))
            $session = New-PSSession -VMId $request.vmId -Credential $credential
        }
        catch {
            Reply @{ ok = $false; stage = 'connect'; error = $_.Exception.Message }
            exit 0
        }

        $inspect = {
            param($name)
            $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if (-not $user) { return @{ exists = $false; isLocal = $false; enabled = $false; inRemoteDesktopUsers = $false } }
            $members = @(Get-LocalGroupMember -SID 'S-1-5-32-555' -ErrorAction SilentlyContinue)
            @{
                exists = $true
                isLocal = ([string]$user.PrincipalSource -eq 'Local')
                enabled = [bool]$user.Enabled
                inRemoteDesktopUsers = [bool]($members | Where-Object { $_.SID -eq $user.SID })
            }
        }

        $provision = {
            param($name, [securestring]$password, [bool]$enableRemoteDesktop)
            $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($user) {
                if ([string]$user.PrincipalSource -ne 'Local') { throw "NOT_LOCAL:An account named $name exists but is not a local account." }
                Set-LocalUser -Name $name -Password $password -PasswordNeverExpires $true -AccountNeverExpires
                Enable-LocalUser -Name $name
            }
            else {
                New-LocalUser -Name $name -Password $password -PasswordNeverExpires -AccountNeverExpires -UserMayNotChangePassword `
                    -FullName 'HyperHarbor' -Description 'HyperHarbor Remote Desktop account' | Out-Null
            }
            $user = Get-LocalUser -Name $name
            $members = @(Get-LocalGroupMember -SID 'S-1-5-32-555' -ErrorAction SilentlyContinue)
            if (-not ($members | Where-Object { $_.SID -eq $user.SID })) { Add-LocalGroupMember -SID 'S-1-5-32-555' -Member $user }
            if ($enableRemoteDesktop) {
                Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
                Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752'
            }
        }

        $setPassword = {
            param($name, [securestring]$password)
            Set-LocalUser -Name $name -Password $password
        }

        # A change from the current password keeps the account's DPAPI data readable; a reset does not. Accounts
        # HyperHarbor made may not change their own password, which a change counts as, so that is allowed first.
        $changePassword = {
            param($name, [securestring]$current, [securestring]$password)
            $ErrorActionPreference = 'Stop'
            function Plain([securestring]$secure) {
                $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
                try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
            }
            $user = Get-LocalUser -Name $name
            if (-not $user.UserMayChangePassword) { Set-LocalUser -Name $name -UserMayChangePassword $true }
            try {
                ([adsi]"WinNT://$env:COMPUTERNAME/$name,user").ChangePassword((Plain $current), (Plain $password))
                @{ changed = $true; reason = $null }
            }
            catch {
                $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
                Set-LocalUser -Name $name -Password $password
                @{ changed = $false; reason = $reason }
            }
        }

        try {
            switch ($request.operation) {
                'inspect' { $result = Invoke-Command -Session $session -ScriptBlock $inspect -ArgumentList $request.accountName }
                'provision' {
                    Invoke-Command -Session $session -ScriptBlock $provision -ArgumentList $request.accountName, (Secure $request.accountPassword), ([bool]$request.enableRemoteDesktop)
                    $result = $null
                }
                'setPassword' {
                    Invoke-Command -Session $session -ScriptBlock $setPassword -ArgumentList $request.accountName, (Secure $request.accountPassword)
                    $result = $null
                }
                'changePassword' {
                    $how = Invoke-Command -Session $session -ScriptBlock $changePassword -ArgumentList $request.accountName, (Secure $request.currentPassword), (Secure $request.accountPassword)
                    $result = @{ changed = [bool]$how.changed; reason = [string]$how.reason }
                }
                default { throw "Unknown operation $($request.operation)." }
            }
            Reply @{ ok = $true; result = $result }
        }
        catch {
            $message = $_.Exception.Message
            if ($message.StartsWith('NOT_LOCAL:')) { Reply @{ ok = $false; stage = 'notLocal'; error = $message.Substring(10) } }
            else { Reply @{ ok = $false; stage = 'guest'; error = $message } }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
        """;
}
