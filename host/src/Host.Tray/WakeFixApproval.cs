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

        var start = new ProcessStartInfo(serviceExecutable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(WakeFixHelper.Switch);
        start.ArgumentList.Add(string.Join(',', request.Fixes.Select(fix => fix.CheckId)));

        try
        {
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new WakeFixCompletedMessage(request.RequestId, "applied", null)
                : new WakeFixCompletedMessage(request.RequestId, "failed", $"The fix helper exited with code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new WakeFixCompletedMessage(request.RequestId, "declined", "The administrator prompt was cancelled.");
        }
    }
}
