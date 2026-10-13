using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Hosts;

/// <summary>What the host does with a running VM while a game is in the foreground. Schema: GameVmAction.</summary>
public enum GameVmAction
{
    /// <summary>Freeze it in memory. The default, and the only choice for a VM with a GPU partition.</summary>
    Pause,

    /// <summary>Save its memory to disk and stop it; it is started again afterwards.</summary>
    Save,

    /// <summary>Leave it running.</summary>
    Keep,
}

/// <summary>Schema: GameVmPolicy.</summary>
/// <param name="AlwaysPause">Apply the action even while someone is connected to the VM.</param>
public sealed record GameVmPolicy(
    [property: JsonRequired] Guid VmId,
    [property: JsonRequired] GameVmAction Action,
    bool AlwaysPause = false);

/// <summary>Game-aware mode choices. Schema: GameAwareSettings.</summary>
/// <param name="Allowlist">Programs (lower case, no .exe) that count as a game whenever they are in the foreground.</param>
/// <param name="IgnoreList">Programs that never count, for false positives.</param>
/// <param name="VmPolicies">VMs with their own action; the others use <paramref name="DefaultAction"/>.</param>
public sealed record GameAwareSettings(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] GameVmAction DefaultAction,
    IReadOnlyList<string> Allowlist,
    IReadOnlyList<string> IgnoreList,
    IReadOnlyList<GameVmPolicy> VmPolicies);

/// <summary>Schema: GameAwareVm.</summary>
/// <param name="GpuPartition">The VM is in Performance mode, so it is always paused (never saved or kept running).</param>
public sealed record GameAwareVm(Guid VmId, string Name, bool GpuPartition);

/// <summary>Schema: GameAwareState.</summary>
/// <param name="Gaming">A game is in the foreground now.</param>
/// <param name="Process">The program that started the game state.</param>
/// <param name="Reason">Why the host thinks so, or why not.</param>
/// <param name="ChangedVms">Names of the VMs the host paused or saved and will resume.</param>
/// <param name="SessionVms">Names of VMs left running because someone is connected; clients show "host is gaming".</param>
public sealed record GameAwareState(
    bool Enabled,
    bool Gaming,
    IReadOnlyList<string> ChangedVms,
    IReadOnlyList<string> SessionVms,
    string? Process = null,
    string? Reason = null,
    DateTimeOffset? Since = null);

/// <summary>Schema: GameAwareInfo.</summary>
public sealed record GameAwareInfo(GameAwareSettings Settings, GameAwareState State, IReadOnlyList<GameAwareVm> Vms);

/// <summary>Schema: NotAGameResult.</summary>
/// <param name="Ignored">The program added to the ignore list; absent when no game state was active.</param>
public sealed record NotAGameResult(string? Ignored = null);
