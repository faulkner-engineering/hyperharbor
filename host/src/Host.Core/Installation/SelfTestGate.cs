using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>One check of a self-test run.</summary>
public sealed record SelfTestCheck(string Name, bool Passed, string Detail);

/// <summary>What HyperHarbor.Host.exe --self-test writes to its result file.</summary>
public sealed record SelfTestResult(string Version, bool Succeeded, IReadOnlyList<SelfTestCheck> Checks)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}

/// <summary>Whether a new version may be installed, and why not.</summary>
public sealed record SelfTestOutcome(bool Passed, string Detail, SelfTestResult? Result = null);

/// <summary>Runs an executable and waits for it, for the self-test gate. Tests supply a fake.</summary>
public interface ISelfTestProcess
{
    /// <returns>The exit code and standard error, or null when it did not finish in time (it is then stopped).</returns>
    Task<(int ExitCode, string StandardError)?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The gate a downloaded version must pass before it is installed: it runs
/// "HyperHarbor.Host.exe --self-test &lt;copy&gt; &lt;result file&gt;" against a copy of the data directory (the new
/// version may migrate its data, and must never touch the real data before it is installed), then requires a
/// zero exit code and a result file that reports success for the expected version. The copy and the result
/// file are deleted afterwards.
/// </summary>
public sealed class SelfTestGate(string dataDirectory, ISelfTestProcess process, TimeSpan timeout)
{
    public const string Switch = "--self-test";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    private const int MaxErrorLength = 500;

    public async Task<SelfTestOutcome> RunAsync(string executable, SemanticVersion expected, CancellationToken cancellationToken)
    {
        var work = Path.Combine(dataDirectory, DataBackup.UpdateFolderName, "selftest");
        Directory.CreateDirectory(work);
        var copy = Path.Combine(work, $"{expected}-{Guid.NewGuid():N}");
        var resultFile = copy + ".result.json";
        try
        {
            DataBackup.Copy(dataDirectory, copy);
            var run = await process.RunAsync(executable, [Switch, copy, resultFile], timeout, cancellationToken).ConfigureAwait(false);
            if (run is not { } finished)
            {
                return new SelfTestOutcome(false, $"The self-test did not finish within {timeout.TotalSeconds:0} seconds.");
            }

            var result = ReadResult(resultFile);
            if (finished.ExitCode != 0 || result is not { Succeeded: true })
            {
                return new SelfTestOutcome(false, Describe(finished, result), result);
            }

            return SemanticVersion.TryParse(result.Version, out var reported) && reported == expected
                ? new SelfTestOutcome(true, $"Version {expected} passed its self-test.", result)
                : new SelfTestOutcome(false, $"The self-test reported version {result.Version}, not {expected}.", result);
        }
        finally
        {
            DataBackup.Delete(copy);
            File.Delete(resultFile);
        }
    }

    private static SelfTestResult? ReadResult(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SelfTestResult>(Utf8Json.ReadFile(path), SelfTestResult.JsonOptions) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe((int ExitCode, string StandardError) run, SelfTestResult? result)
    {
        var failed = result?.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.Detail}").ToList() ?? [];
        if (failed.Count > 0)
        {
            return "The self-test failed. " + string.Join(" ", failed);
        }

        var error = run.StandardError.Trim();
        var detail = error.Length == 0 ? "It wrote no result." : error.Length > MaxErrorLength ? error[..MaxErrorLength] + "…" : error;
        return $"The self-test exited with code {run.ExitCode}. {detail}";
    }
}

/// <summary>Runs the self-test as a child process with no window, collecting its standard error.</summary>
public sealed class SelfTestProcess : ISelfTestProcess
{
    public async Task<(int ExitCode, string StandardError)?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
        var error = new StringBuilder();
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not null)
            {
                lock (error)
                {
                    error.AppendLine(line.Data);
                }
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        lock (error)
        {
            return (process.ExitCode, error.ToString());
        }
    }
}
