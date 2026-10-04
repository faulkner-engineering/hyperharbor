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

    private static Task<JsonNode?> RunAsync(object payload, CancellationToken cancellationToken) =>
        PowerShellDirectRunner.RunAsync(EncodedScript, payload, Timeout, cancellationToken);

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
