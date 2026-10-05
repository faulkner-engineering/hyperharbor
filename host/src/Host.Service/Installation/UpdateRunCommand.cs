using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Service.Logging;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// hh-update.exe update-run, started by the "HyperHarbor\Update" task as SYSTEM: applies a handed-off update or
/// finishes an interrupted one (<see cref="UpdateApplier"/>). Its log lines go to the host's daily log file.
/// Exit code 0, except 1 when the update ended Failed and 2 when it was not run elevated.
/// </summary>
internal static class UpdateRunCommand
{
    private const string MutexName = @"Global\HyperHarbor.Update";

    public static async Task<int> RunAsync()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            return 2;
        }

        using var single = new Mutex(initiallyOwned: false, MutexName);
        bool acquired;
        try
        {
            acquired = single.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // A previous helper was killed mid-update; this run recovers from its saved state.
            acquired = true;
        }

        if (!acquired)
        {
            return 0;
        }

        try
        {
            var dataDirectory = HostIdentityStore.DefaultDataDirectory;
            var layout = UninstallEntry.FindInstalled() ?? new InstallLayout(InstallLayout.DefaultRoot);
            var trayUser = TrayUser.Resolve(new ConfigurationBuilder().Build(), isWindowsService: true);
            using var logging = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddProvider(new FileLoggerProvider(dataDirectory, readers: trayUser is null ? [] : [trayUser])));
            var logger = logging.CreateLogger("HyperHarbor.Update");
            var service = new WindowsServiceControl();
            var applier = new UpdateApplier(
                layout,
                dataDirectory,
                new UpdateStateStore(dataDirectory),
                service,
                new HostHealthProbe(dataDirectory, service),
                TimeProvider.System,
                logger);

            try
            {
                var state = await applier.RunAsync(CancellationToken.None);
                return state.Phase == UpdatePhase.Failed ? 1 : 0;
            }
            catch (Exception ex)
            {
                // The state stays as saved, so the next run (or the next startup) resumes from it.
                logger.LogCritical(ex, "The update helper stopped unexpectedly.");
                return 3;
            }
        }
        finally
        {
            single.ReleaseMutex();
        }
    }
}
