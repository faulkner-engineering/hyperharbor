using System.ComponentModel;
using System.Diagnostics;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Asks the user to approve Wake-on-LAN fixes requested by a paired device, then runs the host
/// service executable elevated in its fix-only mode.
/// </summary>
internal static class WakeFixApproval
{
    // ERROR_CANCELLED: the user declined the UAC prompt.
    private const int ErrorCancelled = 1223;

    public static async Task<WakeFixCompletedMessage> HandleAsync(WakeFixRequestedMessage request, string? serviceExecutable)
    {
        var list = string.Join(Environment.NewLine, request.Fixes.Select(fix => $"  •  {fix.Title}"));
        var answer = MessageBox.Show(
            $"\"{request.RequestedBy}\" asked to change these settings so it can wake this PC over the network:{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}Windows will ask for administrator permission. Apply them?",
            "HyperHarbor Wake-on-LAN",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2,
            MessageBoxOptions.DefaultDesktopOnly);

        if (answer != DialogResult.Yes)
        {
            return new WakeFixCompletedMessage(request.RequestId, "declined", "The user declined at the host.");
        }

        if (serviceExecutable is null || !File.Exists(serviceExecutable))
        {
            return new WakeFixCompletedMessage(request.RequestId, "failed", "The host service executable could not be located.");
        }

        // The elevated helper writes per-check results here, since its output cannot be captured.
        var resultFile = Path.Combine(Path.GetTempPath(), $"hyperharbor-wake-fix-{request.RequestId:N}.json");
        var start = new ProcessStartInfo(serviceExecutable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(WakeFixHelper.Switch);
        start.ArgumentList.Add(string.Join(',', request.Fixes.Select(fix => fix.CheckId)));
        start.ArgumentList.Add(resultFile);

        try
        {
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            var detail = ReadResults(resultFile);
            return process.ExitCode == 0
                ? new WakeFixCompletedMessage(request.RequestId, "applied", detail)
                : new WakeFixCompletedMessage(request.RequestId, "failed", detail ?? $"The fix helper exited with code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new WakeFixCompletedMessage(request.RequestId, "declined", "The administrator prompt was cancelled.");
        }
        finally
        {
            File.Delete(resultFile);
        }
    }

    /// <summary>Summarizes the helper's results, failures first, or null when none were written.</summary>
    private static string? ReadResults(string path)
    {
        try
        {
            var results = System.Text.Json.JsonSerializer.Deserialize<List<HelperResult>>(File.ReadAllText(path));
            return results is null
                ? null
                : string.Join(" ", results
                    .OrderBy(result => result.Applied)
                    .Select(result => $"{result.CheckId}: {(result.Applied ? "applied" : "not applied")}. {result.Detail}"));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record HelperResult(string CheckId, bool Applied, string Detail);
}
