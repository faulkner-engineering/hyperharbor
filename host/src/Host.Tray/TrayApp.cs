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
    private static readonly TimeSpan PreviousTrayTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Runs the tray on its own STA thread and returns when the user exits it.</summary>
    public static int Run(string[] args)
    {
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var first);
        if (!first && args.Contains(TrayRestart.Switch, StringComparer.Ordinal))
        {
            first = WaitForPreviousTray(instance);
        }

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

    /// <summary>A restarted tray waits briefly for the tray that started it to exit and release the mutex.</summary>
    private static bool WaitForPreviousTray(Mutex instance)
    {
        try
        {
            return instance.WaitOne(PreviousTrayTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous tray ended without releasing it; this one owns it now.
            return true;
        }
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
