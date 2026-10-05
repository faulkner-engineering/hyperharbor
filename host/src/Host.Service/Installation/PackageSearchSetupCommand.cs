using System.Diagnostics;
using System.Text;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// The elevated package search helper: HyperHarbor.Host.Exe --setup-package-search [result file]. The tray starts
/// it with a UAC prompt. It installs PowerShell 7 for all users (the MSI through winget, because winget's default
/// for PowerShell 7.6 is a per-user MSIX that SYSTEM cannot run) and the Microsoft.WinGet.Client module for all
/// users, each only when missing. A one-line summary goes to the result file; exit code 0 means success.
/// </summary>
internal static class PackageSearchSetupCommand
{
    public const int ExitSucceeded = 0;
    public const int ExitFailed = 1;
    public const int ExitNotElevated = 2;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static bool Matches(string[] args) => args.Length is 1 or 2 && args[0] == PackageSearchSetupHelper.Switch;

    public static async Task<int> RunAsync(string[] args)
    {
        var resultFile = args.Length == 2 ? args[1] : null;
        if (!Environment.IsPrivilegedProcess)
        {
            return await ReportAsync(resultFile, ExitNotElevated, "Setting up package search requires administrator rights.");
        }

        if (PackageSearchSetupHelper.IsSetUp)
        {
            return await ReportAsync(resultFile, ExitSucceeded, "Package search is already set up: PowerShell 7 and the WinGet PowerShell module are installed for all users.");
        }

        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(Timeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var lastLine = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            await errors;
            return PackageSearchSetupHelper.IsSetUp
                ? await ReportAsync(resultFile, ExitSucceeded, "Package search is set up: PowerShell 7 and the WinGet PowerShell module are installed for all users.")
                : await ReportAsync(resultFile, ExitFailed, lastLine is { Length: > 0 } ? lastLine : "Package search could not be set up.");
        }
        catch (OperationCanceledException)
        {
            return await ReportAsync(resultFile, ExitFailed, "Installing PowerShell 7 or the WinGet module took too long. Try again later.");
        }
        catch (Exception ex)
        {
            return await ReportAsync(resultFile, ExitFailed, ex.Message);
        }
    }

    private static async Task<int> ReportAsync(string? resultFile, int exitCode, string message)
    {
        if (resultFile is not null)
        {
            await File.WriteAllTextAsync(resultFile, message);
        }

        return exitCode;
    }

    /// <summary>Installs only what is missing; the last line it writes is the problem when something fails.</summary>
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $pwsh = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
            if (-not (Test-Path $pwsh)) {
                if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
                    throw 'winget is not available for this account, so PowerShell 7 cannot be installed. Install PowerShell 7 (the MSI from github.com/PowerShell/PowerShell) and try again.'
                }
                & winget.exe install --id Microsoft.PowerShell --exact --scope machine --installer-type wix --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
                if (-not (Test-Path $pwsh)) {
                    throw "PowerShell 7 did not install (winget exit code $LASTEXITCODE)."
                }
            }

            if (-not (Get-Module -ListAvailable -Name Microsoft.WinGet.Client)) {
                $nuget = Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue | Where-Object { $_.Version -ge [version]'2.8.5.201' }
                if (-not $nuget) {
                    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
                }
                Install-Module -Name Microsoft.WinGet.Client -Scope AllUsers -Force -AllowClobber -Repository PSGallery
            }
            'done'
        }
        catch {
            $_.Exception.Message
        }
        """;
}
