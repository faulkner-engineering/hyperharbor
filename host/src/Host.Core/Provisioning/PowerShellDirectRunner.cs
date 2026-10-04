using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// Runs a host-side Windows PowerShell script that talks to a guest over PowerShell Direct. The script is
/// passed with -EncodedCommand and contains no secrets; the request, including credentials, travels as
/// JSON on standard input. The script answers with one JSON line: { ok: true, result } or
/// { ok: false, stage: connect|notLocal|guest, error }. Each feature keeps its own script, so the encoded
/// command stays well under the 32,767-character command line limit.
/// </summary>
internal static class PowerShellDirectRunner
{
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <exception cref="GuestUnavailableException">The guest could not be reached, or the script did not finish within <paramref name="timeout"/>.</exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the administrator credential.</exception>
    /// <exception cref="GuestOperationException">The script failed in the guest.</exception>
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

        using var process = Process.Start(start) ?? throw new GuestOperationException("Windows PowerShell could not be started.");
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
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new GuestUnavailableException("PowerShell Direct did not respond in time. The guest may still be starting.");
        }

        return Interpret(await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
    }

    /// <summary>Maps the script's JSON result to a value or a typed exception.</summary>
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
            throw new GuestOperationException($"Unexpected output from PowerShell Direct. {errors.Trim()}".Trim());
        }

        if ((bool?)result["ok"] == true)
        {
            return result["result"];
        }

        var message = (string?)result["error"] ?? "Unknown error.";
        throw (string?)result["stage"] switch
        {
            "connect" when IsCredentialError(message) => new GuestCredentialRejectedException($"The guest rejected the administrator credential. {message}"),
            "connect" => new GuestUnavailableException($"PowerShell Direct could not connect to the guest. It must be a running Windows guest that has finished starting. {message}"),
            "notLocal" => new GuestAccountConflictException(message),
            _ => new GuestOperationException(message),
        };
    }

    private static bool IsCredentialError(string message) =>
        message.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || message.Contains("password", StringComparison.OrdinalIgnoreCase)
        || message.Contains("logon failure", StringComparison.OrdinalIgnoreCase)
        || message.Contains("access is denied", StringComparison.OrdinalIgnoreCase);
}
