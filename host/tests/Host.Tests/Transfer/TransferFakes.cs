using HyperHarbor.Host.Core.Transfer;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary>Fixed configuration versions.</summary>
internal sealed class FakeHyperVVersions : IHyperVVersions
{
    public string? VmVersion { get; set; } = "12.0";

    public HostVersionCapabilities Capabilities { get; set; } = new(["8.0", "9.0", "10.0", "11.0", "12.0"]);

    public Task<string?> VmVersionAsync(Guid vmId, CancellationToken cancellationToken) => Task.FromResult(VmVersion);

    public Task<HostVersionCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(Capabilities);
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
