using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests;

/// <summary>An in-memory guest: tracks one account per VM and records passwords it was given.</summary>
internal sealed class FakeGuestAccountManager : IGuestAccountManager
{
    public const string FakeHostKey = "SHA256:fake-host-key";

    private readonly object _gate = new();

    public Dictionary<Guid, GuestAccountState> Accounts { get; } = [];

    public List<(Guid VmId, string Account, string Password)> PasswordsSet { get; } = [];

    public List<GuestCredential> AdminCredentialsUsed { get; } = [];

    public List<GuestTarget> TargetsUsed { get; } = [];

    public List<GuestProvisionOptions> ProvisionOptions { get; } = [];

    /// <summary>Thrown by every call when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When true, provisioning "succeeds" but the account is not allowed to sign in remotely.</summary>
    public bool SkipGroupMembership { get; set; }

    /// <summary>Simulated time for a guest call, to exercise concurrency.</summary>
    public TimeSpan Delay { get; set; }

    public async Task<GuestAccountState> InspectAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken)
    {
        await Enter(target, admin);
        lock (_gate)
        {
            return Accounts.GetValueOrDefault(target.VmId, new GuestAccountState(false, false, false, false));
        }
    }

    public async Task<GuestTarget> ProvisionAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string password,
        GuestProvisionOptions options,
        CancellationToken cancellationToken)
    {
        await Enter(target, admin);
        lock (_gate)
        {
            Accounts[target.VmId] = new GuestAccountState(true, true, true, !SkipGroupMembership);
            PasswordsSet.Add((target.VmId, accountName, password));
            ProvisionOptions.Add(options);
        }

        // Like the SSH manager, Linux targets come back with the host key pinned.
        return target.Os == GuestOsFamily.Linux ? target with { SshHostKey = FakeHostKey } : target;
    }

    public async Task SetPasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken)
    {
        await Enter(target, admin);
        lock (_gate)
        {
            PasswordsSet.Add((target.VmId, accountName, password));
        }
    }

    private async Task Enter(GuestTarget target, GuestCredential admin)
    {
        lock (_gate)
        {
            AdminCredentialsUsed.Add(admin);
            TargetsUsed.Add(target);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay);
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
