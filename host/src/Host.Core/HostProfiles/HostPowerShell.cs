using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>
/// Runs a Windows PowerShell script on this host. Like PowerShellDirectRunner, the script travels as -EncodedCommand
/// without any data and the request as JSON on standard input; it answers with one JSON line, { ok: true, result } or
/// { ok: false, error }. Every Lean host script has its own text, to stay under the command line limit.
/// </summary>
internal static class HostPowerShell
{
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <exception cref="HostLeanException">PowerShell failed, timed out, or answered with something else.</exception>
    public static async Task<JsonNode?> RunAsync(string encodedScript, object request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encodedScript })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new HostLeanException("Windows PowerShell could not be started.");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new HostLeanException("Windows PowerShell did not finish in time.");
        }

        return Interpret(await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
    }

    public static JsonNode? Interpret(string output, string errors)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        JsonNode? result;
        try
        {
            result = line is null ? null : JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            result = null;
        }

        if (result is null)
        {
            throw new HostLeanException($"Unexpected output from Windows PowerShell. {errors.Trim()}".Trim());
        }

        if ((bool?)result["ok"] == true)
        {
            return result["result"];
        }

        throw new HostLeanException((string?)result["error"] ?? "Unknown error.");
    }
}
