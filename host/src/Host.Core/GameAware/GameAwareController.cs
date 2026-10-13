using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.GameAware;

public enum GameAction
{
    Pause,
    Save,
    Skip,
}

/// <param name="Vm">A running VM.</param>
/// <param name="GpuPartition">The VM is in Performance mode (it has a GPU partition).</param>
/// <param name="HasSession">Someone is connected to it.</param>
public sealed record GameVmInput(Vm Vm, bool GpuPartition, bool HasSession);

/// <param name="SkippedForSession">The VM was left running only because someone is connected; the client shows a notice.</param>
public sealed record GamePlanItem(Vm Vm, GameAction Action, string Reason, bool SkippedForSession = false);

/// <summary>Chooses what to do with each VM when a game starts. Pure.</summary>
public static class GamePolicyPlanner
{
    public static IReadOnlyList<GamePlanItem> Plan(IEnumerable<GameVmInput> inputs, GameAwareSettings settings) =>
        inputs.Where(input => input.Vm.State == VmState.Running).Select(input => PlanOne(input, settings)).ToList();

    private static GamePlanItem PlanOne(GameVmInput input, GameAwareSettings settings)
    {
        var policy = settings.PolicyFor(input.Vm.Id);
        if (policy.Policy == GamePolicy.Keep && !input.GpuPartition)
        {
            return new GamePlanItem(input.Vm, GameAction.Skip, "Its policy keeps it running.");
        }

        if (input.HasSession && !policy.AlwaysPause)
        {
            return new GamePlanItem(input.Vm, GameAction.Skip, "Someone is connected to it.", SkippedForSession: true);
        }

        // A GPU partition cannot be saved, and the user's choice to keep it running would starve the game of the GPU.
        if (input.GpuPartition)
        {
            return new GamePlanItem(input.Vm, GameAction.Pause, "It has a GPU partition, so it is paused.");
        }

        return policy.Policy == GamePolicy.Save
            ? new GamePlanItem(input.Vm, GameAction.Save, "Its policy saves it.")
            : new GamePlanItem(input.Vm, GameAction.Pause, "Its policy pauses it.");
    }
}

/// <summary>Pauses and resumes a VM in memory (Hyper-V's Paused state). Saving goes through the power service.</summary>
public interface IVmPauser
{
    Task PauseAsync(Guid vmId, CancellationToken cancellationToken);

    Task ResumeAsync(Guid vmId, CancellationToken cancellationToken);
}

/// <summary>A VM this host paused or saved for a game, kept on disk so a host restart still resumes it.</summary>
public sealed record GameChangedVm(Guid VmId, string Name, GameAction Action, string? Process, DateTimeOffset At);

/// <summary>What the tray reported about the foreground.</summary>
public sealed record GameStateReport(bool Gaming, string? Process, string? Reason);

/// <param name="ChangedVms">Names of the VMs this host paused or saved.</param>
/// <param name="SessionVms">Names of the VMs left running because someone is connected (the client shows "host is gaming").</param>
public sealed record GameAwareStatus(
    bool Enabled,
    bool Gaming,
    string? Process,
    DateTimeOffset? Since,
    IReadOnlyList<string> ChangedVms,
    IReadOnlyList<string> SessionVms);

/// <summary>
/// Game-aware mode on the service. The tray reports the foreground (already smoothed by <see cref="GamingDebouncer"/>);
/// this class pauses or saves the running VMs when a game starts and resumes exactly those it changed when it ends. A
/// watchdog resumes them when reports stop (the tray exited) or no game has been reported for a minute, and a restart of
/// the host resumes whatever the last run left changed.
/// </summary>
public sealed class GameAwareController
{
    public static readonly TimeSpan WatchdogAfter = TimeSpan.FromSeconds(60);
    public const string ChangedFileName = "game-aware-changed.json";

    private static readonly JsonSerializerOptions Json = CreateJson();

    private readonly GameAwareStore _settings;
    private readonly IVmInventory _inventory;
    private readonly IVmPauser _pauser;
    private readonly IHyperVPowerInvoker _power;
    private readonly Func<Vm, CancellationToken, Task<bool>> _hasSession;
    private readonly Func<Guid, bool> _hasGpuPartition;
    private readonly TimeProvider _time;
    private readonly ILogger<GameAwareController> _logger;
    private readonly string _changedPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _gaming;
    private string? _process;
    private DateTimeOffset? _since;
    private DateTimeOffset _lastGameReport;
    private List<string> _sessionVms = [];

    public GameAwareController(
        GameAwareStore settings,
        IVmInventory inventory,
        IVmPauser pauser,
        IHyperVPowerInvoker power,
        Func<Vm, CancellationToken, Task<bool>> hasSession,
        Func<Guid, bool> hasGpuPartition,
        TimeProvider time,
        ILogger<GameAwareController> logger,
        string dataDirectory)
    {
        _settings = settings;
        _inventory = inventory;
        _pauser = pauser;
        _power = power;
        _hasSession = hasSession;
        _hasGpuPartition = hasGpuPartition;
        _time = time;
        _logger = logger;
        _changedPath = Path.Combine(dataDirectory, ChangedFileName);
    }

    /// <summary>The state the tray reports, with the VMs this host changed.</summary>
    public GameAwareStatus Status()
    {
        var changed = ReadChanged().Select(c => c.Name).ToList();
        return new GameAwareStatus(_settings.Load().Enabled, _gaming, _process, _since, changed, _sessionVms);
    }

    /// <summary>Called for each report from the tray, about every five seconds.</summary>
    public async Task ReportAsync(GameStateReport report, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var enabled = _settings.Load().Enabled;
            if (report.Gaming)
            {
                _lastGameReport = _time.GetUtcNow();
                _process = report.Process ?? _process;
            }

            if (report.Gaming && enabled && !_gaming)
            {
                await EnterAsync(report, cancellationToken).ConfigureAwait(false);
            }
            else if (!report.Gaming && _gaming)
            {
                await ExitAsync("the game ended", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The watchdog, run about every ten seconds and once at start: resumes when gaming lost its reports, when the mode was
    /// turned off, and VMs a previous run left changed.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_gaming)
            {
                if (!_settings.Load().Enabled)
                {
                    await ExitAsync("game-aware mode was turned off", cancellationToken).ConfigureAwait(false);
                }
                else if (_time.GetUtcNow() - _lastGameReport >= WatchdogAfter)
                {
                    await ExitAsync("no game was reported for a minute (the tray may have stopped)", cancellationToken).ConfigureAwait(false);
                }
            }
            else if (ReadChanged().Count > 0)
            {
                await ResumeAsync("the host found VMs a game had changed", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnterAsync(GameStateReport report, CancellationToken cancellationToken)
    {
        _gaming = true;
        _since = _time.GetUtcNow();
        _process = report.Process;
        _sessionVms = [];
        var settings = _settings.Load();
        var vms = await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);
        var inputs = new List<GameVmInput>();
        foreach (var vm in vms.Where(v => v.State == VmState.Running))
        {
            inputs.Add(new GameVmInput(vm, _hasGpuPartition(vm.Id), await _hasSession(vm, cancellationToken).ConfigureAwait(false)));
        }

        _logger.LogInformation("A game is in the foreground ({Process}): {Reason}", report.Process ?? "unknown program", report.Reason ?? "reported by the tray");
        var changed = ReadChanged();
        foreach (var item in GamePolicyPlanner.Plan(inputs, settings))
        {
            if (item.Action == GameAction.Skip)
            {
                _logger.LogInformation("Leaving {Vm} running while {Process} is in the foreground: {Reason}", item.Vm.Name, report.Process, item.Reason);
                if (item.SkippedForSession)
                {
                    _sessionVms.Add(item.Vm.Name);
                }

                continue;
            }

            try
            {
                if (item.Action == GameAction.Pause)
                {
                    await _pauser.PauseAsync(item.Vm.Id, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _power.InvokeAsync(item.Vm.Id, VmAction.Save, cancellationToken).ConfigureAwait(false);
                }

                changed.Add(new GameChangedVm(item.Vm.Id, item.Vm.Name, item.Action, report.Process, _time.GetUtcNow()));
                WriteChanged(changed);
                _logger.LogInformation(
                    "{Action} {Vm} because {Process} is in the foreground. {Reason}",
                    item.Action == GameAction.Pause ? "Paused" : "Saved", item.Vm.Name, report.Process ?? "a game", item.Reason);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not {Action} {Vm} for the game: {Message}", item.Action.ToString().ToLowerInvariant(), item.Vm.Name, ex.Message);
            }
        }
    }

    private async Task ExitAsync(string why, CancellationToken cancellationToken)
    {
        _gaming = false;
        _since = null;
        _sessionVms = [];
        await ResumeAsync(why, cancellationToken).ConfigureAwait(false);
        _process = null;
    }

    /// <summary>Resumes only the VMs this host changed. A VM someone else already started or turned off is just forgotten.</summary>
    private async Task ResumeAsync(string why, CancellationToken cancellationToken)
    {
        var remaining = new List<GameChangedVm>();
        foreach (var entry in ReadChanged())
        {
            try
            {
                var vm = await _inventory.GetAsync(entry.VmId, cancellationToken).ConfigureAwait(false);
                if (vm is null)
                {
                    continue;
                }

                if (entry.Action == GameAction.Pause && vm.State == VmState.Paused)
                {
                    await _pauser.ResumeAsync(entry.VmId, cancellationToken).ConfigureAwait(false);
                }
                else if (entry.Action == GameAction.Save && vm.State == VmState.Saved)
                {
                    await _power.InvokeAsync(entry.VmId, VmAction.Start, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogInformation("{Vm} is {State}, so it is left as it is.", entry.Name, vm.State);
                    continue;
                }

                _logger.LogInformation("Resumed {Vm}: {Why}. It was changed because {Process} was in the foreground.", entry.Name, why, entry.Process ?? "a game");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not resume {Vm}: {Message}. It stays on the list and is tried again.", entry.Name, ex.Message);
                remaining.Add(entry);
            }
        }

        WriteChanged(remaining);
    }

    private List<GameChangedVm> ReadChanged()
    {
        try
        {
            return File.Exists(_changedPath)
                ? JsonSerializer.Deserialize<List<GameChangedVm>>(Utf8Json.ReadFile(_changedPath), Json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteChanged(List<GameChangedVm> changed)
    {
        if (changed.Count == 0)
        {
            File.Delete(_changedPath);
            return;
        }

        AtomicJson.Write(_changedPath, changed, Json);
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
