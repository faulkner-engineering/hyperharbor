namespace HyperHarbor.Host.Core.Updates;

public enum UpdateMode
{
    /// <summary>Check, download, and install when the host is idle or at the maintenance time.</summary>
    Auto,

    /// <summary>Check and download, but install only when the owner asks (tray: Install now).</summary>
    Notify,

    /// <summary>Do not check.</summary>
    Off,
}

/// <summary>When to check for updates and when an automatic install may start. Pure, for tests.</summary>
public static class UpdateSchedule
{
    public static bool IsCheckDue(DateTimeOffset? lastCheck, TimeSpan interval, DateTimeOffset now) =>
        lastCheck is not { } last || now - last >= interval;

    /// <summary>
    /// Auto mode installs when nothing has been in progress for <paramref name="idleTime"/>, or, inside the window
    /// that opens at <paramref name="maintenanceTime"/> (host local time), as soon as nothing is in progress.
    /// Nothing is ever installed while something is in progress.
    /// </summary>
    public static bool MayInstallAutomatically(
        UpdateMode mode,
        bool inProgress,
        DateTimeOffset idleSince,
        DateTimeOffset now,
        TimeSpan idleTime,
        TimeOnly? maintenanceTime,
        TimeSpan maintenanceWindow,
        TimeZoneInfo zone)
    {
        if (mode != UpdateMode.Auto || inProgress)
        {
            return false;
        }

        if (now - idleSince >= idleTime)
        {
            return true;
        }

        return maintenanceTime is { } start && IsInWindow(TimeZoneInfo.ConvertTime(now, zone), start, maintenanceWindow);
    }

    private static bool IsInWindow(DateTimeOffset local, TimeOnly start, TimeSpan window)
    {
        // The window may cross midnight, so measure from the most recent start.
        var sinceStart = TimeOnly.FromDateTime(local.DateTime) - start;
        return sinceStart >= TimeSpan.Zero ? sinceStart < window : sinceStart + TimeSpan.FromDays(1) < window;
    }
}
