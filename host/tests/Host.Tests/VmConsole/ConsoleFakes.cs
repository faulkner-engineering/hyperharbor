using HyperHarbor.Host.Core.VmConsole;

namespace HyperHarbor.Host.Tests.VmConsole;

/// <summary>Changes passwords in memory, rejecting a wrong old password like NetUserChangePassword.</summary>
internal sealed class FakeConsolePasswordChanger : IConsolePasswordChanger
{
    private readonly object _gate = new();

    /// <summary>The current password of each account; an account missing here does not exist.</summary>
    public Dictionary<string, string> Passwords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Account, string NewPassword)> Changes { get; } = [];

    public void ChangePassword(string accountName, string oldPassword, string newPassword)
    {
        lock (_gate)
        {
            if (!Passwords.TryGetValue(accountName, out var current))
            {
                throw ConsoleConflictException.SetupRequired($"The host console account {accountName} does not exist.");
            }

            if (current != oldPassword)
            {
                throw ConsoleConflictException.SetupRequired($"The stored password for the host console account {accountName} is out of date.");
            }

            Passwords[accountName] = newPassword;
            Changes.Add((accountName, newPassword));
        }
    }
}

internal sealed class FakeConsoleAccess : IConsoleAccessGranter
{
    public List<(Guid VmId, string Trustee)> Grants { get; } = [];

    public List<string> Revokes { get; } = [];

    public Task GrantAsync(Guid vmId, string trustee, CancellationToken cancellationToken)
    {
        Grants.Add((vmId, trustee));
        return Task.CompletedTask;
    }

    public Task RevokeEverywhereAsync(string trustee, CancellationToken cancellationToken)
    {
        Revokes.Add(trustee);
        return Task.CompletedTask;
    }
}

internal sealed class FakeLocalAccountAdmin : ILocalAccountAdmin
{
    public Dictionary<string, string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Restricted { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Deleted { get; } = [];

    public bool Exists(string accountName) => Accounts.ContainsKey(accountName);

    public void Create(string accountName, string password, string comment)
    {
        if (!Accounts.TryAdd(accountName, password))
        {
            throw new InvalidOperationException($"{accountName} already exists.");
        }
    }

    public void Reset(string accountName, string password)
    {
        if (!Accounts.ContainsKey(accountName))
        {
            throw new InvalidOperationException($"{accountName} does not exist.");
        }

        Accounts[accountName] = password;
    }

    public void Restrict(string accountName) => Restricted.Add(accountName);

    public void Delete(string accountName)
    {
        Accounts.Remove(accountName);
        Restricted.Remove(accountName);
        Deleted.Add(accountName);
    }
}
