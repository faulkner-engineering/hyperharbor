using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>Host local account operations that need administrator rights.</summary>
public interface ILocalAccountAdmin
{
    bool Exists(string accountName);

    void Create(string accountName, string password, string comment);

    void Reset(string accountName, string password);

    void Restrict(string accountName);

    void Delete(string accountName);
}

/// <param name="Created">False when an existing account was reset.</param>
public sealed record ConsoleAccountResult(string UserName, string AccountName, bool Created);

/// <summary>
/// Creates or removes the host console accounts, one per User. Runs in the elevated helper started by
/// the tray or by Start-HyperHarbor.ps1, since creating local accounts needs administrator rights.
/// Setting up again is safe: an existing account gets a new password and the same restrictions.
/// </summary>
public sealed class ConsoleAccountSetup
{
    public const string AccountComment = "HyperHarbor console access. Managed by HyperHarbor; do not use directly.";

    private readonly UserStore _users;
    private readonly ConsoleAccountStore _store;
    private readonly ILocalAccountAdmin _accounts;
    private readonly IConsoleAccessGranter _access;

    public ConsoleAccountSetup(UserStore users, ConsoleAccountStore store, ILocalAccountAdmin accounts, IConsoleAccessGranter access)
    {
        _users = users;
        _store = store;
        _accounts = accounts;
        _access = access;
    }

    /// <summary>Creates or resets the console account of every User.</summary>
    public IReadOnlyList<ConsoleAccountResult> SetUp()
    {
        _users.GetOrCreateDefault();
        var results = new List<ConsoleAccountResult>();
        foreach (var user in _users.List())
        {
            var accountName = ConsoleAccountName.For(user.Name);
            var password = PasswordGenerator.Generate();
            var created = !_accounts.Exists(accountName);
            if (created)
            {
                _accounts.Create(accountName, password, AccountComment);
            }
            else
            {
                _accounts.Reset(accountName, password);
            }

            _accounts.Restrict(accountName);
            _store.Save(user.UserId, new ConsoleCredential(accountName, password));
            results.Add(new ConsoleAccountResult(user.Name, accountName, created));
        }

        return results;
    }

    /// <summary>Revokes console access, then deletes every stored console account and its profile.</summary>
    public async Task<IReadOnlyList<string>> RemoveAsync(CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        foreach (var (userId, credential) in _store.List())
        {
            if (_accounts.Exists(credential.AccountName))
            {
                await _access.RevokeEverywhereAsync(ConsoleService.Trustee(credential.AccountName), cancellationToken).ConfigureAwait(false);
                _accounts.Delete(credential.AccountName);
            }

            _store.Remove(userId);
            removed.Add(credential.AccountName);
        }

        return removed;
    }
}
