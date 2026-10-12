using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// The sleep schedule of Console idle: the task "HyperHarbor\Console sleep" runs the helper's scheduled-sleep command at
/// the sleep time (and again every <see cref="RetryMinutes"/> minutes while it reports that something is in use), and
/// "HyperHarbor\Console wake" wakes the PC at the wake time. Both run as SYSTEM, also on battery, from an XML definition.
/// </summary>
internal sealed class PowerScheduleTasks(InstallLayout layout) : IPowerSchedule
{
    public const string SleepTaskName = @"HyperHarbor\Console sleep";
    public const string WakeTaskName = @"HyperHarbor\Console wake";

    /// <summary>How often the sleep task tries again while a remote session, job, or game blocks it.</summary>
    public const int RetryMinutes = 15;

    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public bool IsRegistered() => Query(SleepTaskName) && Query(WakeTaskName);

    public void Register(TimeOnly sleepAt, TimeOnly wakeAt)
    {
        Create(SleepTaskName, SleepDefinition(layout, sleepAt, wakeAt));
        try
        {
            Create(WakeTaskName, WakeDefinition(wakeAt));
        }
        catch (InvalidOperationException)
        {
            Remove();
            throw;
        }
    }

    public void Remove()
    {
        foreach (var name in new[] { SleepTaskName, WakeTaskName })
        {
            try
            {
                Schtasks("/Delete", "/TN", name, "/F");
            }
            catch (InvalidOperationException)
            {
                // Not registered.
            }
        }
    }

    /// <summary>The sleep task. The task scheduler restarts it after a failed run, and the command fails while the host is in use.</summary>
    internal static XDocument SleepDefinition(InstallLayout layout, TimeOnly sleepAt, TimeOnly wakeAt)
    {
        var hours = wakeAt.ToTimeSpan() - sleepAt.ToTimeSpan();
        if (hours <= TimeSpan.Zero)
        {
            hours += TimeSpan.FromDays(1);
        }

        var retries = Math.Max(1, (int)(hours.TotalMinutes / RetryMinutes));
        return Definition(
            "Puts the HyperHarbor host to sleep at the scheduled time when no remote session, job, or game is in use.",
            sleepAt,
            wakeToRun: false,
            new XElement(task("Command"), layout.HelperExecutable),
            new XElement(task("Arguments"), HostCommandLine.ScheduledSleepVerb),
            new XElement(
                task("RestartOnFailure"),
                new XElement(task("Interval"), $"PT{RetryMinutes}M"),
                new XElement(task("Count"), Math.Min(retries, 999).ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>The wake task: wakes the PC, then does nothing (the power plan keeps it awake).</summary>
    internal static XDocument WakeDefinition(TimeOnly wakeAt) => Definition(
        "Wakes the HyperHarbor host at the scheduled time.",
        wakeAt,
        wakeToRun: true,
        new XElement(task("Command"), Path.Combine(Environment.SystemDirectory, "cmd.exe")),
        new XElement(task("Arguments"), "/c exit 0"),
        null);

    private static XName task(string name) => Task + name;

    private static XDocument Definition(string description, TimeOnly at, bool wakeToRun, XElement command, XElement arguments, XElement? restart)
    {
        var settings = new List<object>
        {
            new XElement(Task + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(Task + "DisallowStartIfOnBatteries", "false"),
            new XElement(Task + "StopIfGoingOnBatteries", "false"),
            new XElement(Task + "StartWhenAvailable", "false"),
            new XElement(Task + "RunOnlyIfNetworkAvailable", "false"),
            new XElement(Task + "RunOnlyIfIdle", "false"),
            new XElement(Task + "AllowStartOnDemand", "true"),
            new XElement(Task + "Enabled", "true"),
            new XElement(Task + "WakeToRun", wakeToRun ? "true" : "false"),
            new XElement(Task + "ExecutionTimeLimit", "PT5M"),
        };
        if (restart is not null)
        {
            settings.Add(restart);
        }

        return new XDocument(
            new XElement(
                Task + "Task",
                new XAttribute("version", "1.2"),
                new XElement(Task + "RegistrationInfo", new XElement(Task + "Description", description)),
                new XElement(
                    Task + "Triggers",
                    new XElement(
                        Task + "CalendarTrigger",
                        new XElement(Task + "StartBoundary", $"2026-01-01T{at:HH:mm}:00"),
                        new XElement(Task + "Enabled", "true"),
                        new XElement(Task + "ScheduleByDay", new XElement(Task + "DaysInterval", "1")))),
                new XElement(
                    Task + "Principals",
                    new XElement(
                        Task + "Principal",
                        new XAttribute("id", "System"),
                        new XElement(Task + "UserId", "S-1-5-18"),
                        new XElement(Task + "RunLevel", "HighestAvailable"))),
                new XElement(Task + "Settings", settings),
                new XElement(Task + "Actions", new XAttribute("Context", "System"), new XElement(Task + "Exec", command, arguments))));
    }

    private static void Create(string name, XDocument definition)
    {
        var file = Path.Combine(Path.GetTempPath(), $"hyperharbor-power-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, definition.ToString(), System.Text.Encoding.Unicode);
            Schtasks("/Create", "/TN", name, "/XML", file, "/F");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static bool Query(string name)
    {
        try
        {
            Schtasks("/Query", "/TN", name);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void Schtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"schtasks {arguments[0]} failed (exit code {process.ExitCode}). {error}");
        }
    }
}
