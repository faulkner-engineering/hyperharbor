using System.Diagnostics;
using System.Text;

namespace HyperHarbor.Host.Core.Backup.Kopia;

/// <summary>One kopia command line. Secrets travel in <see cref="Environment"/>, never in the arguments.</summary>
public sealed record KopiaInvocation(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout)
{
    public override string ToString() => $"kopia {string.Join(' ', Arguments.Take(3))} ...";
}

public sealed record KopiaResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>The seam between HyperHarbor and the kopia executable; tests replace it.</summary>
public interface IKopiaRunner
{
    Task<KopiaResult> RunAsync(KopiaInvocation invocation, CancellationToken cancellationToken);
}

public sealed class KopiaException(string message, KopiaFailure failure) : Exception(message)
{
    public KopiaFailure Failure { get; } = failure;
}

public enum KopiaFailure
{
    Unknown,
    BadPassphrase,
    NotARepository,
    Unreachable,
    AccessDenied,
    TimedOut,
}

/// <summary>Starts the bundled kopia.exe. The caller verifies the executable's hash before constructing this.</summary>
public sealed class KopiaProcessRunner(string executablePath) : IKopiaRunner
{
    public async Task<KopiaResult> RunAsync(KopiaInvocation invocation, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in invocation.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in invocation.Environment)
        {
            start.Environment[name] = value;
        }

        start.Environment["KOPIA_CHECK_FOR_UPDATES"] = "false";

        using var process = Process.Start(start) ?? throw new KopiaException("kopia did not start.", KopiaFailure.Unknown);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(invocation.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new KopiaException($"kopia did not finish within {invocation.Timeout.TotalMinutes:0} minutes and was stopped.", KopiaFailure.TimedOut);
        }

        return new KopiaResult(process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
    }
}
