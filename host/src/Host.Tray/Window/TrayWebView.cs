using Microsoft.Web.WebView2.Core;

namespace HyperHarbor.Host.Tray.Window;

/// <summary>
/// The WebView2 environment all tray windows share, with its data in %LOCALAPPDATA%\HyperHarbor\TrayWebView (the
/// tray runs as the signed-in user). Windows 11 includes the WebView2 Runtime; without it the tray falls back to
/// notifications.
/// </summary>
internal static class TrayWebView
{
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>The installed WebView2 Runtime's version, or null when there is none.</summary>
    public static string? RuntimeVersion
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>Created once, on first use, on the calling (UI) thread.</summary>
    public static Task<CoreWebView2Environment> EnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarbor", "TrayWebView"));
}
