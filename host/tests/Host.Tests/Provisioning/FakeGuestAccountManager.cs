using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Tests;

/// <summary>An in-memory guest: tracks one account per VM and records passwords it was given.</summary>
internal sealed class FakeGuestAccountManager : IGuestAccountManager
{
    private readonly object _gate = new();

    public Dictionary<Guid, GuestAccountState> Accounts { get; } = [];

    public List<(Guid VmId, string Account, string Password)> PasswordsSet { get; } = [];

    public List<GuestCredential> AdminCredentialsUsed { get; } = [];

    /// <summary>Thrown by every call when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When true, provisioning "succeeds" but the account is left out of Remote Desktop Users.</summary>
    public bool SkipGroupMembership { get; set; }

    /// <summary>Simulated time for a guest call, to exercise concurrency.</summary>
    public TimeSpan Delay { get; set; }

    public async Task<GuestAccountState> InspectAsync(Guid vmId, GuestCredential admin, string accountName, CancellationToken cancellationToken)
    {
        await Enter(admin);
        lock (_gate)
        {
            return Accounts.GetValueOrDefault(vmId, new GuestAccountState(false, false, false, false));
        }
    }

    public async Task ProvisionAsync(Guid vmId, GuestCredential admin, string accountName, string password, bool enableRemoteDesktop, CancellationToken cancellationToken)
    {
        await Enter(admin);
        lock (_gate)
        {
            Accounts[vmId] = new GuestAccountState(true, true, true, !SkipGroupMembership);
            PasswordsSet.Add((vmId, accountName, password));
        }
    }

    public async Task SetPasswordAsync(Guid vmId, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken)
    {
        await Enter(admin);
        lock (_gate)
        {
            PasswordsSet.Add((vmId, accountName, password));
        }
    }

    private async Task Enter(GuestCredential admin)
    {
        lock (_gate)
        {
            AdminCredentialsUsed.Add(admin);
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
