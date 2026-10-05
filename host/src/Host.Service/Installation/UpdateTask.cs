using System.Diagnostics;
using System.Xml.Linq;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// The scheduled task "HyperHarbor\Update": runs root\hh-update.exe update-run as SYSTEM at every startup (to
/// finish an update a restart interrupted) and on demand (the service hands an update off by starting it).
/// A task, rather than a child process of the service, survives the service stopping.
/// </summary>
internal static class UpdateTask
{
    public const string Name = @"HyperHarbor\Update";

    /// <summary>
    /// Registers the task from an XML definition: schtasks /Create on its own makes tasks that start only on AC
    /// power, so a laptop host on battery would never update or finish an interrupted update.
    /// </summary>
    public static void Register(InstallLayout layout)
    {
        var definition = Path.Combine(Path.GetTempPath(), $"hyperharbor-update-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(definition, Definition(layout).ToString(), System.Text.Encoding.Unicode);
            Schtasks("/Create", "/TN", Name, "/XML", definition, "/F");
        }
        finally
        {
            File.Delete(definition);
        }
    }

    /// <summary>The task definition. Exposed for tests.</summary>
    internal static XDocument Definition(InstallLayout layout)
    {
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(
            new XElement(
                task + "Task",
                new XAttribute("version", "1.2"),
                new XElement(task + "RegistrationInfo", new XElement(task + "Description", "Installs HyperHarbor host updates and finishes one a restart interrupted.")),
                new XElement(task + "Triggers", new XElement(task + "BootTrigger", new XElement(task + "Enabled", "true"))),
                new XElement(
                    task + "Principals",
                    new XElement(
                        task + "Principal",
                        new XAttribute("id", "System"),
                        new XElement(task + "UserId", "S-1-5-18"),
                        new XElement(task + "RunLevel", "HighestAvailable"))),
                new XElement(
                    task + "Settings",
                    new XElement(task + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(task + "DisallowStartIfOnBatteries", "false"),
                    new XElement(task + "StopIfGoingOnBatteries", "false"),
                    new XElement(task + "StartWhenAvailable", "true"),
                    new XElement(task + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(task + "RunOnlyIfIdle", "false"),
                    new XElement(task + "AllowStartOnDemand", "true"),
                    new XElement(task + "Enabled", "true"),
                    new XElement(task + "ExecutionTimeLimit", "PT1H")),
                new XElement(
                    task + "Actions",
                    new XAttribute("Context", "System"),
                    new XElement(
                        task + "Exec",
                        new XElement(task + "Command", layout.HelperExecutable),
                        new XElement(task + "Arguments", HostCommandLine.UpdateRunVerb)))));
    }

    /// <summary>Starts the task now. It runs once and ends; the service calls this to hand an update off.</summary>
    public static void Run() => Schtasks("/Run", "/TN", Name);

    public static void Delete()
    {
        try
        {
            Schtasks("/Delete", "/TN", Name, "/F");
        }
        catch (InvalidOperationException)
        {
            // Not registered.
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
            throw new InvalidOperationException($"schtasks {arguments[0]} {Name} failed (exit code {process.ExitCode}). {error}");
        }
    }
}
