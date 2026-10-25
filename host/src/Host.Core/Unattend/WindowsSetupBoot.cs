using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>How a VM's most recent boot ended, from Hyper-V's own log.</summary>
public enum VmBootOutcome
{
    /// <summary>No boot was logged in the period asked about, or the log cannot be read.</summary>
    Unknown,

    /// <summary>The firmware handed over to a boot loader (event 18601).</summary>
    Booted,

    /// <summary>Every boot device failed and the VM stopped at the firmware's boot summary (event 18603).</summary>
    NothingToBoot,
}

/// <summary>Reads how a VM's boots ended.</summary>
public interface IVmBootEvents
{
    /// <summary>The outcome of the VM's most recent boot logged at or after <paramref name="since"/>.</summary>
    VmBootOutcome LastBoot(Guid vmId, DateTimeOffset since);
}

/// <summary>
/// Events 18601 ("successfully booted an operating system") and 18603 ("failed to boot an operating system") in the
/// Microsoft-Windows-Hyper-V-Worker-Admin log. 18601 is logged whenever the firmware starts any boot loader, including
/// the DVD's "Press any key" loader; 18603 only when every boot device failed.
/// </summary>
public sealed class HyperVWorkerBootEvents : IVmBootEvents
{
    public const string LogName = "Microsoft-Windows-Hyper-V-Worker-Admin";
    public const int BootedEventId = 18601;
    public const int NothingToBootEventId = 18603;

    public VmBootOutcome LastBoot(Guid vmId, DateTimeOffset since) => LastBoot(vmId, since, until: null);

    /// <summary>Like <see cref="LastBoot(Guid, DateTimeOffset)"/>, ignoring events after <paramref name="until"/>. For live tests.</summary>
    internal static VmBootOutcome LastBoot(Guid vmId, DateTimeOffset since, DateTimeOffset? until)
    {
        static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var window = $"@SystemTime>='{Utc(since)}'" + (until is { } end ? $" and @SystemTime<='{Utc(end)}'" : string.Empty);
        var query = new EventLogQuery(
            LogName,
            PathType.LogName,
            $"*[System[(EventID={BootedEventId} or EventID={NothingToBootEventId}) and TimeCreated[{window}]]]")
        {
            ReverseDirection = true,
        };

        // The VM's ID is in the event's user data; the newest event for this VM decides.
        var marker = $"<VmId>{vmId.ToString("D").ToUpperInvariant()}</VmId>";
        try
        {
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.ToXml().Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return record.Id == NothingToBootEventId ? VmBootOutcome.NothingToBoot : VmBootOutcome.Booted;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return VmBootOutcome.Unknown;
        }

        return VmBootOutcome.Unknown;
    }
}

/// <summary>Gets Windows installation media past "Press any key to boot from CD or DVD".</summary>
public static class WindowsBootKey
{
    /// <summary>VK_SPACE.</summary>
    public const int SpaceKey = 0x20;

    /// <summary>Windows media wait about five seconds for a key; Space is pressed for longer than that.</summary>
    public const int Presses = 12;

    /// <summary>
    /// Presses Space once per <paramref name="interval"/>, <see cref="Presses"/> times. Presses fail harmlessly while
    /// the VM is still starting.
    /// </summary>
    public static async Task PressAsync(IVmKeyboard keyboard, Guid vmId, TimeSpan interval, TimeProvider time, ILogger logger, CancellationToken cancellationToken)
    {
        for (var press = 0; press < Presses; press++)
        {
            await Task.Delay(interval, time, cancellationToken).ConfigureAwait(false);
            try
            {
                await keyboard.TypeKeyAsync(vmId, SpaceKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "A key press for VM {VmId} was not delivered.", vmId);
            }
        }
    }
}

/// <summary>What <see cref="WindowsSetupBootRecovery.RecoverAsync"/> did.</summary>
public enum BootRecoveryResult
{
    /// <summary>The VM is not stuck.</summary>
    None,

    /// <summary>The VM found nothing to boot; it was restarted and the boot key pressed again.</summary>
    Restarted,

    /// <summary>It found nothing to boot again after <see cref="WindowsSetupBootRecovery.MaxRestarts"/> restarts.</summary>
    GaveUp,
}

/// <summary>
/// Restarts a Windows install whose VM found nothing to boot. The boot key is pressed on the first boot only (so later
/// restarts boot the installed system), but the guest can reset before Setup has written anything to the disk (seen
/// 26 seconds into the first boot of build 26300 media on 2026-10-25, cause unknown); that next boot's "Press any key" prompt then times out, the
/// empty disk does not boot, and the VM stops at the firmware's boot summary for good. Hyper-V logs that as event
/// 18603, which only happens when no device booted, so restarting the VM and pressing the key again can never disturb
/// an installed system. The newest boot event decides, so a host restart does not act on a failure already recovered.
/// </summary>
public sealed class WindowsSetupBootRecovery
{
    public const int MaxRestarts = 3;

    private readonly IVmBootEvents _events;
    private readonly IHyperVPowerInvoker _power;
    private readonly IVmKeyboard _keyboard;
    private readonly TimeProvider _time;
    private readonly ILogger<WindowsSetupBootRecovery> _logger;
    private readonly TimeSpan _keyInterval;
    private readonly ConcurrentDictionary<Guid, int> _restarts = new();

    /// <param name="keyInterval">Time between key presses; one second unless a test shortens it.</param>
    public WindowsSetupBootRecovery(
        IVmBootEvents events,
        IHyperVPowerInvoker power,
        IVmKeyboard keyboard,
        TimeProvider time,
        ILogger<WindowsSetupBootRecovery> logger,
        TimeSpan? keyInterval = null)
    {
        _events = events;
        _power = power;
        _keyboard = keyboard;
        _time = time;
        _logger = logger;
        _keyInterval = keyInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Checks a Windows install still in Setup, and restarts its VM when it found nothing to boot.</summary>
    public async Task<BootRecoveryResult> RecoverAsync(UnattendedInstall install, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (install.Os != Shared.Contracts.Unattend.InstallOs.Windows
            || _events.LastBoot(install.VmId, install.StartedAt) != VmBootOutcome.NothingToBoot)
        {
            return BootRecoveryResult.None;
        }

        var restarts = _restarts.AddOrUpdate(install.VmId, 1, (_, count) => count + 1);
        if (restarts > MaxRestarts)
        {
            return BootRecoveryResult.GaveUp;
        }

        _logger.LogWarning(
            "VM {VmId} found nothing to boot during Windows Setup; restarting it and pressing the boot key again ({Attempt} of {Max}).",
            install.VmId,
            restarts,
            MaxRestarts);
        await _power.InvokeAsync(install.VmId, VmAction.TurnOff, cancellationToken).ConfigureAwait(false);
        await _power.InvokeAsync(install.VmId, VmAction.Start, cancellationToken).ConfigureAwait(false);
        await WindowsBootKey.PressAsync(_keyboard, install.VmId, _keyInterval, _time, _logger, cancellationToken).ConfigureAwait(false);
        return BootRecoveryResult.Restarted;
    }
}
