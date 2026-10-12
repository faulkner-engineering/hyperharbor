using System.Runtime.InteropServices;
using System.Text.Json;
using HyperHarbor.Shared.Contracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HyperHarbor.Host.Tray.Ambient;

/// <summary>
/// The Windows screensaver of the Ambient screen (HyperHarbor Ambient.scr is a copy of the host executable). Windows
/// starts it with /s after the idle time, /c to configure, /p for the preview in its dialog. It shows a black page with
/// a clock in WebView2 and ends on keyboard or mouse input, like any screensaver; the tray ends it on controller input.
/// </summary>
public static class AmbientScreensaverApp
{
    /// <summary>Input in the first moments after the screen starts (or the display comes on) is not taken as dismissal.</summary>
    internal static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan HintShown = TimeSpan.FromSeconds(5);

    /// <param name="args">The screensaver switches Windows passes: /s, /c, or /p followed by a window handle.</param>
    /// <param name="gpuLoad">The share of the GPU the given processes used, or null when Windows does not report it. Provided by the host, which can read the counters.</param>
    public static int Run(string[] args, Func<IReadOnlySet<int>, double?> gpuLoad)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(gpuLoad);

        switch (Mode(args))
        {
            case ScreensaverMode.Show:
                return Show(gpuLoad);
            case ScreensaverMode.Configure:
                MessageBox.Show(
                    "The ambient screen is set up in the HyperHarbor Host window, under Game ready.",
                    "HyperHarbor Ambient",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            default:
                // The small preview in Windows' screensaver dialog stays empty.
                return 0;
        }
    }

    internal enum ScreensaverMode
    {
        Show,
        Configure,
        Preview,
    }

    /// <summary>/s shows; /p (with a handle) previews; /c or nothing configures. Windows may write the switch with a colon (/c:1234) or a dash.</summary>
    internal static ScreensaverMode Mode(string[] args)
    {
        if (args.Length == 0)
        {
            return ScreensaverMode.Configure;
        }

        var first = args[0].TrimStart('/', '-').ToLowerInvariant();
        return first switch
        {
            _ when first.StartsWith('s') => ScreensaverMode.Show,
            _ when first.StartsWith('p') => ScreensaverMode.Preview,
            _ when first.StartsWith('a') => ScreensaverMode.Preview,
            _ => ScreensaverMode.Configure,
        };
    }

    private static int Show(Func<IReadOnlySet<int>, double?> gpuLoad)
    {
        using var running = new Mutex(initiallyOwned: true, AmbientNames.RunningMutex, out var first);
        if (!first)
        {
            return 0;
        }

        using var dismiss = new EventWaitHandle(false, EventResetMode.AutoReset, AmbientNames.DismissEvent);
        using var hint = new EventWaitHandle(false, EventResetMode.AutoReset, AmbientNames.HintEvent);
        using var job = JobAccounting.TryCreate();

        var thread = new Thread(() => RunMessageLoop(dismiss, hint, job, gpuLoad)) { Name = "Ambient" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        running.ReleaseMutex();
        return 0;
    }

    private static void RunMessageLoop(EventWaitHandle dismiss, EventWaitHandle hint, JobAccounting? job, Func<IReadOnlySet<int>, double?> gpuLoad)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        using var context = new AmbientContext(job, gpuLoad);
        var dismissed = ThreadPool.RegisterWaitForSingleObject(dismiss, (_, _) => ui.Post(_ => context.End(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        var hinted = ThreadPool.RegisterWaitForSingleObject(hint, (_, _) => ui.Post(_ => context.ShowHint(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            Application.Run(context);
        }
        finally
        {
            dismissed.Unregister(null);
            hinted.Unregister(null);
        }
    }

    /// <summary>One full-screen form per monitor; the primary one shows the page, the others stay black.</summary>
    private sealed class AmbientContext : ApplicationContext
    {
        private readonly List<Form> _forms = [];
        private readonly AmbientPageForm _page;
        private readonly System.Windows.Forms.Timer _poll;
        private readonly DisplayStateWatcher _display;
        private readonly AmbientMeter _meter = new(TimeProvider.System, Environment.ProcessorCount);
        private readonly JobAccounting? _job;
        private readonly Func<IReadOnlySet<int>, double?> _gpuLoad;
        private readonly TimeProvider _time = TimeProvider.System;
        private uint _baseline;
        private DateTimeOffset _graceUntil;
        private bool _displayOff;
        private bool _ending;
        private int _measuring;

        public AmbientContext(JobAccounting? job, Func<IReadOnlySet<int>, double?> gpuLoad)
        {
            _job = job;
            _gpuLoad = gpuLoad;
            _baseline = LastInputTick();
            _graceUntil = _time.GetUtcNow() + StartGrace;

            _page = new AmbientPageForm();
            foreach (var screen in Screen.AllScreens)
            {
                Form form = screen.Primary ? _page : new Form();
                Black(form, screen.Bounds);
                _forms.Add(form);
            }

            if (!_forms.Contains(_page))
            {
                Black(_page, Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720));
                _forms.Add(_page);
            }

            Cursor.Hide();
            foreach (var form in _forms)
            {
                form.Show();
            }

            _page.Closed += (_, _) => End();
            _ = _page.StartAsync();

            _display = new DisplayStateWatcher(off =>
            {
                _displayOff = off;
                if (!off)
                {
                    // The display came on again: stay on the screen for a moment and ignore the input that woke it.
                    _baseline = LastInputTick();
                    _graceUntil = _time.GetUtcNow() + AmbientDismissal.WakeGrace;
                }
            });

            _poll = new System.Windows.Forms.Timer { Interval = 500 };
            _poll.Tick += (_, _) => Poll();
            _poll.Start();
        }

        public void ShowHint() => _page.ShowHint(HintShown);

        public void End()
        {
            if (_ending)
            {
                return;
            }

            _ending = true;
            _poll.Stop();
            Cursor.Show();
            foreach (var form in _forms)
            {
                form.Hide();
            }

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _poll.Dispose();
                _display.Dispose();
                foreach (var form in _forms)
                {
                    form.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void Poll()
        {
            if (_ending)
            {
                return;
            }

            if (!_displayOff)
            {
                var input = LastInputTick();
                if (input != _baseline)
                {
                    if (_time.GetUtcNow() < _graceUntil)
                    {
                        _baseline = input;
                    }
                    else
                    {
                        End();
                        return;
                    }
                }

                _page.PushData();
            }

            Measure();
        }

        /// <summary>Reports the cost of the screen now and then. The GPU counters are read off the UI thread; they take a couple of seconds.</summary>
        private void Measure()
        {
            var ticks = _job?.CpuTicks() ?? (ulong)System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.Ticks;
            if (_meter.Tick(ticks) is not { } report || Interlocked.Exchange(ref _measuring, 1) == 1)
            {
                return;
            }

            var ids = _job?.ProcessIds() ?? new HashSet<int> { Environment.ProcessId };
            _ = Task.Run(() =>
            {
                try
                {
                    var gpu = _gpuLoad(ids);
                    AmbientFiles.Write(AmbientNames.MetricsFile, new AmbientMetrics(DateTimeOffset.UtcNow, report.CpuPercent, gpu, report.Seconds));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // The figures are skipped this time.
                }
                finally
                {
                    Interlocked.Exchange(ref _measuring, 0);
                }
            });
        }

        private static void Black(Form form, Rectangle bounds)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.StartPosition = FormStartPosition.Manual;
            form.ShowInTaskbar = false;
            form.TopMost = true;
            form.BackColor = Color.Black;
            form.Bounds = bounds;
        }
    }

    internal static uint LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>
/// The primary monitor's form: WebView2 showing the ambient page. It uses its own WebView2 data folder and no GPU
/// acceleration, so a clock on black costs as little as possible, and it posts the page's data when the data file changes.
/// </summary>
internal sealed class AmbientPageForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private DateTime _dataWritten;
    private bool _ready;

    public AmbientPageForm()
    {
        BackColor = Color.Black;
        Controls.Add(_webView);
    }

    public async Task StartAsync()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarbor", "AmbientWebView");
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-gpu --disable-background-networking" };
        var environment = await CoreWebView2Environment.CreateAsync(null, folder, options);
        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;

        core.AddWebResourceRequestedFilter(Window.WebWindow.Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => Window.WebWindow.Serve(environment, e);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Window.WebWindow.Origin, StringComparison.Ordinal))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += (_, e) =>
        {
            if (!_ready && e.WebMessageAsJson.Contains("\"ready\"", StringComparison.Ordinal))
            {
                _ready = true;
                _dataWritten = default;
                PushData();
            }
        };
        core.Navigate($"{Window.WebWindow.Origin}index.html#/ambient");
    }

    public void ShowHint(TimeSpan duration) => Post(new { type = "hint", seconds = (int)duration.TotalSeconds });

    /// <summary>Sends the data file to the page when it changed since the last time. Cheap when it did not.</summary>
    public void PushData()
    {
        if (!_ready)
        {
            return;
        }

        var written = File.GetLastWriteTimeUtc(AmbientNames.ScreenDataFile);
        if (written == _dataWritten)
        {
            return;
        }

        _dataWritten = written;
        var data = AmbientFiles.Read<AmbientScreenData>(AmbientNames.ScreenDataFile);
        Post(new { type = "data", hostStatus = data?.HostStatus, runningVms = data?.RunningVms, devices = data?.Devices, chordHint = data?.ChordHint });
    }

    private void Post(object message)
    {
        if (_ready && _webView.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, ContractJson.Options));
        }
    }
}
