namespace HyperHarbor.Host.Tray;

/// <summary>
/// The tray (HyperHarbor.Host.exe --tray). One tray runs per sign-in session: starting another asks the
/// running one to open its HyperHarbor Host window, so double-clicking the executable or its shortcut
/// always ends with the window in front.
/// </summary>
public static class TrayApp
{
    private const string InstanceMutexName = @"Local\HyperHarbor.Tray";
    private const string ShowWindowEventName = @"Local\HyperHarbor.Tray.ShowWindow";

    /// <summary>Runs the tray on its own STA thread and returns when the user exits it.</summary>
    public static int Run()
    {
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var show))
            {
                using (show)
                {
                    show.Set();
                }
            }

            return 0;
        }

        using var showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var thread = new Thread(() => RunMessageLoop(showWindow)) { Name = "Tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        instance.ReleaseMutex();
        return 0;
    }

    private static void RunMessageLoop(EventWaitHandle showWindow)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        using var context = new TrayApplicationContext();
        var registration = ThreadPool.RegisterWaitForSingleObject(
            showWindow,
            (_, _) => ui.Post(_ => context.ShowHost(), null),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        try
        {
            Application.Run(context);
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
