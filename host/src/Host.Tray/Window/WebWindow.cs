using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace HyperHarbor.Host.Tray.Window;

/// <summary>
/// A window that shows one page of the tray's HTML app in WebView2. The page is served from this assembly
/// (<see cref="TrayUiResources"/>) at <see cref="Origin"/>; nothing else loads in it. Links to other sites open in
/// the default browser when <see cref="IsExternalLinkAllowed"/> allows them. The window stays hidden until the page
/// says it has drawn itself ("ready"), so it never flashes white.
/// </summary>
internal sealed class WebWindow : Form
{
    public const string Origin = "https://tray.hyperharbor.invalid/";

    // The contract's options (camelCase, enums as strings, nulls left out) so the update status matches GET /host/update.
    private static readonly JsonSerializerOptions Json = Shared.Contracts.ContractJson.Options;

    private readonly WebView2 _webView;
    private readonly string _route;
    private readonly List<string> _pending = [];
    private readonly Size _size;
    private readonly Size _minimum;
    private bool _ready;
    private bool _showWhenReady;

    /// <param name="route">The app's page, for example "host" or "pin".</param>
    /// <param name="size">The preferred size at 100% scaling; <see cref="PlaceOn"/> scales it for the monitor.</param>
    public WebWindow(string title, string route, Size size, Size minimum)
    {
        _route = route;
        _size = size;
        _minimum = minimum;
        Text = title;
        Icon = Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : null;
        StartPosition = FormStartPosition.Manual;
        // PlaceOn sizes the window for its monitor; WinForms must not scale it again (the page scales itself).
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;

        _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Background };
        Controls.Add(_webView);
    }

    /// <summary>A message from the page (its "type" property says which).</summary>
    public event Action<JsonElement>? MessageReceived;

    public bool IsReady => _ready;

    /// <summary>Closing by the user hides the window instead, so it opens again at once (raises <see cref="Hidden"/>).</summary>
    public bool HideOnClose { get; init; }

    public event Action? Hidden;

    /// <summary>Loads the page; the window shows itself once the page is ready, if <see cref="ShowWhenReady"/> was called.</summary>
    public async Task InitializeAsync(CoreWebView2Environment environment)
    {
        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2;
        var debug = System.Diagnostics.Debugger.IsAttached;
        core.Settings.AreDevToolsEnabled = debug;
        core.Settings.AreDefaultContextMenusEnabled = debug;
        core.Settings.AreBrowserAcceleratorKeysEnabled = debug;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;

        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => Serve(environment, e);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        core.Navigate($"{Origin}index.html#/{_route}");
    }

    /// <summary>
    /// Sizes the window for <paramref name="screen"/>'s scaling (the page itself scales with it) and centers it there,
    /// within the work area. Moving it to another monitor later rescales it (PerMonitorV2).
    /// </summary>
    public void PlaceOn(Screen screen)
    {
        GetDpiForMonitor(MonitorFromPoint(new Point(screen.Bounds.Left + 1, screen.Bounds.Top + 1), 2), 0, out var dpi, out _);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        Size Scaled(Size size) => new((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));

        MinimumSize = Scaled(_minimum);
        var size = WindowFit.Fit(Scaled(_size), screen.WorkingArea, MinimumSize);
        Bounds = WindowFit.Center(size, screen.WorkingArea);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Shows the window now if the page is ready, otherwise as soon as it is.</summary>
    public void ShowWhenReady()
    {
        if (_ready)
        {
            ShowAndActivate();
        }
        else
        {
            _showWhenReady = true;
        }
    }

    /// <summary>Sends a message to the page; messages sent before it is ready are delivered when it is.</summary>
    public void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, message.GetType(), Json);
        if (!_ready)
        {
            _pending.Add(json);
            return;
        }

        _webView.CoreWebView2?.PostWebMessageAsJson(json);
    }

    /// <summary>Only https links to the project's GitHub pages open; anything else is ignored.</summary>
    internal static bool IsExternalLinkAllowed(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && parsed.AbsolutePath.StartsWith("/faulkner-engineering/hyperharbor", StringComparison.OrdinalIgnoreCase);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (HideOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            Hidden?.Invoke();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(Handle);
    }

    private void ShowAndActivate()
    {
        _showWhenReady = false;
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        Activate();
    }

    private void OnMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement.Clone();
        if (message.TryGetProperty("type", out var type) && type.GetString() == "ready" && !_ready)
        {
            _ready = true;
            foreach (var pending in _pending)
            {
                _webView.CoreWebView2.PostWebMessageAsJson(pending);
            }

            _pending.Clear();
            if (_showWhenReady)
            {
                ShowAndActivate();
            }
        }

        MessageReceived?.Invoke(message);
    }

    private static void Serve(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath;
        if (TrayUiResources.Open(path) is not { } file)
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            return;
        }

        // WebView2 reads the stream after the handler returns; it disposes it.
        e.Response = environment.CreateWebResourceResponse(
            file.Content,
            200,
            "OK",
            $"Content-Type: {file.ContentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    private static void OpenExternal(string uri)
    {
        if (IsExternalLinkAllowed(uri))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
    }

    /// <summary>Windows' app theme (light or dark), for the background before the page draws and the title bar.</summary>
    internal static class Theme
    {
        public static bool Dark
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
        }

        /// <summary>The page's --bg in each theme (ui/src/theme.css).</summary>
        public static Color Background => Dark ? Color.FromArgb(0x0d, 0x11, 0x17) : Color.FromArgb(0xf3, 0xf1, 0xec);

        private const int DwmwaUseImmersiveDarkMode = 20;

        public static void ApplyTitleBar(IntPtr window)
        {
            var dark = Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(window, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
