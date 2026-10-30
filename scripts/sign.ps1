<#
.SYNOPSIS
    Signs files with Azure Artifact Signing (formerly Trusted Signing) through signtool, and checks the signature.
.DESCRIPTION
    package.ps1 calls it for the host executable (before latest.json records the executable's SHA-256), and Tauri calls
    it as bundle.windows.signCommand for every file the client installer ships and then the installer itself (before the
    updater's .sig is made over the installer). Signing afterwards would change the bytes those two checks cover.

    Configured by environment variables, which the release workflow sets from repository variables:
        HH_SIGNING_ENDPOINT   the account's endpoint, for example https://eus.codesigning.azure.net
        HH_SIGNING_ACCOUNT    the Artifact Signing account name
        HH_SIGNING_PROFILE    the certificate profile name
    The credential comes from the Azure CLI login (azure/login with OIDC in the release workflow, or az login on a
    developer's PC), through the plug-in's DefaultAzureCredential.

    Without the variables it signs nothing and says so, so local and CI builds need no Azure account; with
    HH_SIGNING_REQUIRED=1 (the release workflow) a missing variable is an error instead. With HH_SIGNING_LOG it also
    appends what it did, and any error, to that file: Tauri hides a sign command's output when it fails.

    -TauriConfig <file> writes the Tauri configuration that makes this script the sign command, run by the same
    PowerShell that runs the caller. A different one fails: Windows PowerShell started from PowerShell 7 through other
    processes (npm, the Tauri CLI) inherits PowerShell 7's module path and cannot load its own modules.
.EXAMPLE
    scripts\sign.ps1 dist\HyperHarbor-Host-0.2.5.exe
    scripts\sign.ps1 -TauriConfig $env:TEMP\tauri.sign.json
#>
# No positional binding: a file passed alone (Tauri passes "%1" alone) must never land in -TauriConfig, which would
# overwrite it with the configuration.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments)][string[]]$Path,
    [string]$TauriConfig
)

$ErrorActionPreference = 'Stop'

if ($TauriConfig) {
    $shell = (Get-Process -Id $PID).Path
    $command = @{ cmd = $shell; args = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '%1') }
    @{ bundle = @{ windows = @{ signCommand = $command } } } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $TauriConfig -Encoding ASCII
    Write-Host "Tauri signs with $shell -File $PSCommandPath" -ForegroundColor Cyan
    exit 0
}
if (-not $Path) { throw 'Name the files to sign, or -TauriConfig <file>.' }

function Write-SigningLog([string]$Line) {
    if ($env:HH_SIGNING_LOG) { Add-Content -LiteralPath $env:HH_SIGNING_LOG -Value "$(Get-Date -Format 'HH:mm:ss') $Line" }
}

# Values pasted into repository variables can carry a line break or a trailing slash, which Azure would take as part of
# the name or the address.
function Clean([string]$Value) { if ($Value) { $Value.Trim() } else { $Value } }
$settings = @{
    Endpoint = (Clean $env:HH_SIGNING_ENDPOINT) -replace '/+$', ''
    CodeSigningAccountName = Clean $env:HH_SIGNING_ACCOUNT
    CertificateProfileName = Clean $env:HH_SIGNING_PROFILE
}
$missing = @($settings.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Name)
if ($missing.Count -gt 0) {
    if ($env:HH_SIGNING_REQUIRED -eq '1') {
        $message = "Signing is required, but HH_SIGNING_ENDPOINT, HH_SIGNING_ACCOUNT, and HH_SIGNING_PROFILE are not all set (missing: $($missing -join ', '))."
        Write-SigningLog $message
        throw $message
    }
    Write-Host "Signing is not configured; left unsigned: $($Path -join ', ')" -ForegroundColor Yellow
    exit 0
}

$metadata = Join-Path ([IO.Path]::GetTempPath()) "hyperharbor-signing-$PID.json"
try {
    # The newest Windows SDK's x64 signtool (the runner images and Visual Studio installs have one).
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe was not found; install the Windows SDK.' }

    $dlib = & (Join-Path $PSScriptRoot 'fetch-tools.ps1') -Tool artifactSigning | Select-Object -Last 1

    # Only the Azure CLI login (and environment variables, for a service principal) are tried: the other credential
    # kinds either cannot work here or wait for a timeout first.
    $settings + @{
        ExcludeCredentials = @(
            'ManagedIdentityCredential', 'WorkloadIdentityCredential', 'SharedTokenCacheCredential', 'VisualStudioCredential',
            'VisualStudioCodeCredential', 'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential'
        )
    } | ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding ASCII

    foreach ($file in $Path) {
        $full = (Resolve-Path -LiteralPath $file).Path
        Write-SigningLog "Signing $full"
        # Windows PowerShell turns a native program's standard error into an exception when errors stop the script, so
        # signtool's error text is collected with the rest and judged by its exit code.
        $ErrorActionPreference = 'Continue'
        $output = & $signtool.FullName sign /v /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $dlib /dmdf $metadata $full 2>&1
        $ErrorActionPreference = 'Stop'
        $output | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            Write-SigningLog (($output | Select-Object -Last 15) -join [Environment]::NewLine)
            throw "signtool could not sign $full (exit code $LASTEXITCODE)."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $full
        if ($signature.Status -ne 'Valid') { throw "$full is not validly signed after signing: $($signature.Status) $($signature.StatusMessage)" }
        Write-SigningLog "Signed $full"
        Write-Host "Signed $full ($($signature.SignerCertificate.Subject))" -ForegroundColor Green
    }
}
catch {
    Write-SigningLog "Failed: $_"
    throw
}
finally {
    Remove-Item -LiteralPath $metadata -ErrorAction SilentlyContinue
}
