using HyperHarbor.Host.Core.Transfer;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary>Hands the sender whatever channel a test set up.</summary>
internal sealed class ChannelFactory(Func<IPeerTransferChannel> open) : IPeerTransferChannelFactory
{
    public IPeerTransferChannel Open(TransferSendRequest request) => open();
}

/// <summary>The API host's channel factory: whatever <see cref="Open"/> a test sets, else a failure.</summary>
internal sealed class FakePeerChannels : IPeerTransferChannelFactory
{
    public Func<TransferSendRequest, IPeerTransferChannel>? OpenChannel { get; set; }

    public List<TransferSendRequest> Requests { get; } = [];

    public IPeerTransferChannel Open(TransferSendRequest request)
    {
        Requests.Add(request);
        return OpenChannel?.Invoke(request) ?? throw new InvalidOperationException("The test set no peer channel.");
    }
}

/// <summary>Fixed configuration versions.</summary>
internal sealed class FakeHyperVVersions : IHyperVVersions
{
    public string? VmVersion { get; set; } = "12.0";

    public HostVersionCapabilities Capabilities { get; set; } = new(["8.0", "9.0", "10.0", "11.0", "12.0"]);

    public Task<string?> VmVersionAsync(Guid vmId, CancellationToken cancellationToken) => Task.FromResult(VmVersion);

    public Task<HostVersionCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(Capabilities);
}

/// <summary>Records the transfer's Hyper-V calls; <see cref="FailOn"/> makes one of them throw.</summary>
internal sealed class FakeHyperVTransfer : IHyperVTransfer
{
    public List<string> Log { get; } = [];

    /// <summary>The ID an import gives; null: a new ID for a copy, <see cref="MoveId"/> for a move.</summary>
    public Guid? ImportedId { get; set; }

    public Guid MoveId { get; set; }

    public ushort OriginalStartupAction { get; set; } = 3;

    public Dictionary<Guid, ushort> StartupActions { get; } = [];

    /// <summary>"export", "import", "fix", "validate", "realize", "delete", or "startup".</summary>
    public string? FailOn { get; set; }

    /// <summary>Runs when the realize step is called, before it fails or succeeds.</summary>
    public Action<Guid>? OnRealize { get; set; }

    /// <summary>Runs inside the export, for example to look at the key protector while it is granted.</summary>
    public Action? OnExport { get; set; }

    /// <summary>Writes a small .vmcx and .vmgs, as Hyper-V's export does (it adds a .VMRS too).</summary>
    public async Task<ExportedDefinition> ExportAsync(Guid vmId, string folder, CancellationToken cancellationToken)
    {
        Call("export");
        OnExport?.Invoke();
        Fail("export");
        var machines = Path.Combine(folder, "VM", "Virtual Machines");
        Directory.CreateDirectory(machines);
        var definition = Path.Combine(machines, $"{vmId:D}.vmcx");
        var state = Path.Combine(machines, $"{vmId:D}.vmgs");
        await File.WriteAllBytesAsync(definition, "<vmcx definition/>"u8.ToArray(), cancellationToken);
        await File.WriteAllBytesAsync(state, "guest state"u8.ToArray(), cancellationToken);
        return new ExportedDefinition(definition, [definition, state]);
    }

    public Task<Guid> ImportPlannedAsync(string definitionFile, bool newIdentity, CancellationToken cancellationToken)
    {
        Call($"import {(newIdentity ? "new" : "same")} {Path.GetFileName(definitionFile)}");
        Fail("import");
        return Task.FromResult(ImportedId ?? (newIdentity ? Guid.NewGuid() : MoveId));
    }

    public Task<ushort> FixPlannedAsync(Guid plannedId, TransferPlan plan, CancellationToken cancellationToken)
    {
        Call("fix");
        Fail("fix");
        return Task.FromResult(OriginalStartupAction);
    }

    public Task ValidatePlannedAsync(Guid plannedId, CancellationToken cancellationToken)
    {
        Call("validate");
        Fail("validate");
        return Task.CompletedTask;
    }

    public Task RealizePlannedAsync(Guid plannedId, CancellationToken cancellationToken)
    {
        Call("realize");
        OnRealize?.Invoke(plannedId);
        Fail("realize");
        return Task.CompletedTask;
    }

    public Task DeletePlannedAsync(Guid plannedId, CancellationToken cancellationToken)
    {
        Call("delete-planned");
        Fail("delete");
        return Task.CompletedTask;
    }

    public Task SetAutomaticStartupActionAsync(Guid vmId, ushort action, CancellationToken cancellationToken)
    {
        Call($"startup {action}");
        Fail("startup");
        StartupActions[vmId] = action;
        return Task.CompletedTask;
    }

    private void Call(string entry) => Log.Add(entry);

    private void Fail(string step)
    {
        if (FailOn == step)
        {
            throw new InvalidOperationException($"{step} failed");
        }
    }
}

/// <summary>
/// One VM's key protector in memory. A grant appends the guardian's name to the bytes, so tests can tell the
/// original from a granted one. Every call is recorded in <see cref="Log"/>.
/// </summary>
internal sealed class FakeKeyProtectorAccess : IKeyProtectorAccess
{
    public static readonly byte[] Original = [1, 2, 3, 4];

    public byte[] Current { get; set; } = Original;

    public List<string> Log { get; } = [];

    public HashSet<string> Guardians { get; } = [];

    public byte[] LocalGuardianExport { get; set; } = [60, 120, 109, 108, 62];

    /// <summary>Set to make the named call ("get", "grant", "set", "set-original", "remove") throw.</summary>
    public string? FailOn { get; set; }

    /// <summary>When set, a get after the original was put back returns these bytes instead.</summary>
    public byte[]? ReadBackAfterRestore { get; set; }

    public Task<byte[]> ExportLocalGuardianAsync(CancellationToken cancellationToken) => Task.FromResult(LocalGuardianExport);

    public Task<byte[]> GetAsync(Guid vmId, CancellationToken cancellationToken)
    {
        Log.Add("get");
        Fail("get");
        return Task.FromResult(ReadBackAfterRestore is { } changed && Log.Contains("set-original") ? changed : Current);
    }

    public Task SetAsync(Guid vmId, byte[] keyProtector, CancellationToken cancellationToken)
    {
        var name = keyProtector.AsSpan().SequenceEqual(Original) ? "set-original" : "set";
        Log.Add(name);
        Fail(name);
        Current = keyProtector;
        return Task.CompletedTask;
    }

    public Task<byte[]> GrantAsync(byte[] keyProtector, byte[] guardianExport, string guardianName, CancellationToken cancellationToken)
    {
        Log.Add("grant");
        Fail("grant");
        Guardians.Add(guardianName);
        return Task.FromResult(keyProtector.Concat(System.Text.Encoding.UTF8.GetBytes(guardianName)).ToArray());
    }

    public Task RemoveGuardianAsync(string guardianName, CancellationToken cancellationToken)
    {
        Log.Add("remove");
        Fail("remove");
        Guardians.Remove(guardianName);
        return Task.CompletedTask;
    }

    private void Fail(string call)
    {
        if (FailOn == call)
        {
            throw new IOException($"{call} failed");
        }
    }
}
