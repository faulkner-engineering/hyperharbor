using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using Wire = HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Core.GameAware;

/// <summary>What the API and the tray show and change for game-aware mode.</summary>
public sealed class GameAwareService(
    GameAwareStore store,
    GameAwareController controller,
    GameAwareMonitor monitor,
    IVmInventory inventory,
    Func<Guid, bool> hasGpuPartition)
{
    public async Task<Wire.GameAwareInfo> GetAsync(CancellationToken cancellationToken)
    {
        var vms = await inventory.ListAsync(cancellationToken).ConfigureAwait(false);
        var state = controller.Status();
        return new Wire.GameAwareInfo(
            ToWire(store.Load()),
            new Wire.GameAwareState(
                state.Enabled, state.Gaming, state.ChangedVms, state.SessionVms, state.Process, monitor.LastVerdict?.Reason, state.Since),
            vms.OrderBy(vm => vm.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(vm => new Wire.GameAwareVm(vm.Id, vm.Name, hasGpuPartition(vm.Id)))
                .ToList());
    }

    /// <exception cref="LifecycleValidationException">A list is too long, or a VM is listed twice.</exception>
    public Wire.GameAwareSettings Apply(Wire.GameAwareSettings request)
    {
        if (request.Allowlist.Count > GameAwareStore.MaxListEntries || request.IgnoreList.Count > GameAwareStore.MaxListEntries)
        {
            throw new LifecycleValidationException(
                $"A list can hold at most {GameAwareStore.MaxListEntries} programs.",
                [new ValidationIssue("allowlist", $"A list can hold at most {GameAwareStore.MaxListEntries} programs.")]);
        }

        if (request.VmPolicies.Select(p => p.VmId).Distinct().Count() != request.VmPolicies.Count)
        {
            throw new LifecycleValidationException("A VM is listed twice.", [new ValidationIssue("vmPolicies", "A VM is listed twice.")]);
        }

        var ignore = request.IgnoreList.Select(GameAwareSettings.NormalizeProcess).ToHashSet();
        return ToWire(store.Update(_ => new GameAwareSettings
        {
            Enabled = request.Enabled,
            DefaultPolicy = ToCore(request.DefaultAction),
            // A program cannot be both: the ignore list wins.
            Allowlist = request.Allowlist.Where(name => !ignore.Contains(GameAwareSettings.NormalizeProcess(name))).ToList(),
            IgnoreList = request.IgnoreList,
            VmPolicies = request.VmPolicies.ToDictionary(p => p.VmId.ToString("D"), p => new GameVmPolicy(ToCore(p.Action), p.AlwaysPause)),
        }));
    }

    public async Task<Wire.NotAGameResult> NotAGameAsync(CancellationToken cancellationToken) =>
        new(await monitor.NotAGameAsync(cancellationToken).ConfigureAwait(false));

    private static Wire.GameAwareSettings ToWire(GameAwareSettings settings) => new(
        settings.Enabled,
        ToWire(settings.DefaultPolicy),
        settings.Allowlist,
        settings.IgnoreList,
        settings.VmPolicies
            .Where(pair => Guid.TryParse(pair.Key, out _))
            .Select(pair => new Wire.GameVmPolicy(Guid.Parse(pair.Key), ToWire(pair.Value.Policy), pair.Value.AlwaysPause))
            .ToList());

    private static Wire.GameVmAction ToWire(GamePolicy policy) => policy switch
    {
        GamePolicy.Save => Wire.GameVmAction.Save,
        GamePolicy.Keep => Wire.GameVmAction.Keep,
        _ => Wire.GameVmAction.Pause,
    };

    private static GamePolicy ToCore(Wire.GameVmAction action) => action switch
    {
        Wire.GameVmAction.Save => GamePolicy.Save,
        Wire.GameVmAction.Keep => GamePolicy.Keep,
        _ => GamePolicy.Pause,
    };
}
