using System.Security.Cryptography;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>A credential for a guest account. ToString never includes the password.</summary>
public sealed record GuestCredential(string UserName, string Password)
{
    public override string ToString() => $"GuestCredential {{ UserName = {UserName} }}";
}

/// <summary>What a password change did: changed from the current password, or reset, and why.</summary>
/// <param name="ResetReason">The guest's error from the change that failed, which can contain no secret; null when it changed.</param>
public sealed record PasswordChange(bool Changed, string? ResetReason)
{
    public static readonly PasswordChange Success = new(true, null);
}

/// <summary>How to reach a guest.</summary>
/// <param name="Os">Selects PowerShell Direct (Windows) or SSH (Linux).</param>
/// <param name="Address">Linux: the guest address for SSH. Not used for Windows.</param>
/// <param name="SshHostKey">
/// Linux: the pinned SHA-256 fingerprint of the guest's SSH host key, or null on first use, when the
/// key presented is accepted and returned by <see cref="IGuestAccountManager.ProvisionAsync"/>.
/// </param>
public sealed record GuestTarget(Guid VmId, GuestOsFamily Os, string? Address = null, string? SshHostKey = null);

/// <param name="EnableRemoteDesktop">Windows: enable Remote Desktop and its firewall rule. Linux: install and enable xrdp.</param>
/// <param name="InstallDesktop">Linux: install Xfce when no desktop environment is installed.</param>
public sealed record GuestProvisionOptions(bool EnableRemoteDesktop, bool InstallDesktop);

/// <param name="IsLocal">Windows: PrincipalSource is Local. Linux: the account is in /etc/passwd (not LDAP or SSSD).</param>
/// <param name="RemoteDesktopAllowed">Windows: member of Remote Desktop Users. Linux: the xrdp service is running.</param>
public sealed record GuestAccountState(bool Exists, bool IsLocal, bool Enabled, bool RemoteDesktopAllowed)
{
    public bool IsReadyForRemoteDesktop => Exists && IsLocal && Enabled && RemoteDesktopAllowed;
}

/// <summary>A User's local administrator account in a Windows guest, used for administrator windows.</summary>
/// <param name="InAdministrators">Member of Administrators (S-1-5-32-544).</param>
/// <param name="InRemoteDesktopUsers">Member of Remote Desktop Users (S-1-5-32-555); it must never be.</param>
/// <param name="DeniedNetworkLogon">Holds SeDenyNetworkLogonRight.</param>
/// <param name="DeniedRemoteInteractiveLogon">Holds SeDenyRemoteInteractiveLogonRight.</param>
public sealed record GuestAdminAccountState(
    bool Exists,
    bool Enabled,
    bool InAdministrators,
    bool InRemoteDesktopUsers,
    bool DeniedNetworkLogon,
    bool DeniedRemoteInteractiveLogon)
{
    /// <summary>The account can answer a User Account Control prompt and cannot sign in over the network or Remote Desktop.</summary>
    public bool IsReadyForElevation =>
        Exists && Enabled && InAdministrators && !InRemoteDesktopUsers && DeniedNetworkLogon && DeniedRemoteInteractiveLogon;
}

/// <summary>
/// Manages a User's local account inside a guest. Every call authenticates to the guest with the
/// stored administrator credential.
/// </summary>
public interface IGuestAccountManager
{
    Task<GuestAccountState> InspectAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the local account, or resets it when it already exists, and allows it to sign in over
    /// Remote Desktop. Returns the target to use from now on (Linux: with the SSH host key pinned).
    /// </summary>
    /// <exception cref="GuestAccountConflictException">An account with this name exists and is not local.</exception>
    Task<GuestTarget> ProvisionAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string password,
        GuestProvisionOptions options,
        CancellationToken cancellationToken);

    Task SetPasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the password from <paramref name="currentPassword"/>, as the account itself would, so the data Windows protected
    /// with it (DPAPI) stays readable; falls back to a reset when the current password no longer works.
    /// </summary>
    /// <returns>Whether it was changed, or why it had to be reset (Linux guests always reset).</returns>
    Task<PasswordChange> ChangePasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string currentPassword, string password, CancellationToken cancellationToken);

    /// <summary>Windows only: the state of a User's administrator account.</summary>
    Task<GuestAdminAccountState> InspectAdminAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken);

    /// <summary>
    /// Windows only: creates the administrator account when it is missing (a member of Administrators and not of
    /// Remote Desktop Users, password never expires, denied network and Remote Desktop sign-in), then sets its
    /// password and enables it. Returns its state afterwards.
    /// </summary>
    /// <exception cref="GuestAccountConflictException">An account with this name exists and is not local.</exception>
    Task<GuestAdminAccountState> EnableAdminAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string fullName,
        string password,
        CancellationToken cancellationToken);

    /// <summary>
    /// Windows only: ends an administrator window by setting a random password the host forgets. The account stays
    /// enabled, so User Account Control prompts keep offering it. Does nothing when it does not exist.
    /// </summary>
    Task LockAdminAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken);
}

/// <summary>Sends each call to the manager for the guest's OS family.</summary>
public sealed class GuestAccountRouter : IGuestAccountManager
{
    private readonly IGuestAccountManager _windows;
    private readonly IGuestAccountManager _linux;

    public GuestAccountRouter(IGuestAccountManager windows, IGuestAccountManager linux)
    {
        _windows = windows;
        _linux = linux;
    }

    public Task<GuestAccountState> InspectAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken) =>
        For(target).InspectAsync(target, admin, accountName, cancellationToken);

    public Task<GuestTarget> ProvisionAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string password,
        GuestProvisionOptions options,
        CancellationToken cancellationToken) =>
        For(target).ProvisionAsync(target, admin, accountName, password, options, cancellationToken);

    public Task SetPasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken) =>
        For(target).SetPasswordAsync(target, admin, accountName, password, cancellationToken);

    public Task<PasswordChange> ChangePasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string currentPassword, string password, CancellationToken cancellationToken) =>
        For(target).ChangePasswordAsync(target, admin, accountName, currentPassword, password, cancellationToken);

    public Task<GuestAdminAccountState> InspectAdminAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken) =>
        For(target).InspectAdminAsync(target, admin, accountName, cancellationToken);

    public Task<GuestAdminAccountState> EnableAdminAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string fullName,
        string password,
        CancellationToken cancellationToken) =>
        For(target).EnableAdminAsync(target, admin, accountName, fullName, password, cancellationToken);

    public Task LockAdminAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken) =>
        For(target).LockAdminAsync(target, admin, accountName, password, cancellationToken);

    private IGuestAccountManager For(GuestTarget target) => target.Os switch
    {
        GuestOsFamily.Windows => _windows,
        GuestOsFamily.Linux => _linux,
        _ => throw new GuestAccountConflictException("The guest operating system is unknown. Wait for the VM to finish starting."),
    };
}

/// <summary>The guest rejected the administrator credential (422).</summary>
public sealed class GuestCredentialRejectedException(string message) : Exception(message);

/// <summary>The guest could not be reached: not running, still starting, or its management channel is off (503).</summary>
public sealed class GuestUnavailableException(string message) : Exception(message);

/// <summary>A command inside the guest failed (502).</summary>
public sealed class GuestOperationException(string message) : Exception(message);

/// <summary>The request conflicts with the VM or account state (409).</summary>
public sealed class GuestAccountConflictException(string message) : Exception(message);

/// <summary>
/// Guest error messages can include text the guest wrote (stderr, script output). They reach logs and
/// the API response, so they are cleaned first: known secrets are replaced, control characters
/// removed, and the length capped.
/// </summary>
public static class GuestErrors
{
    public const int MaxMessageLength = 1000;

    private const string Redacted = "[redacted]";

    public static bool IsGuestError(Exception exception) => exception is GuestCredentialRejectedException
        or GuestUnavailableException
        or GuestOperationException
        or GuestAccountConflictException;

    /// <summary>
    /// Returns an exception of the same type with a cleaned message. The original is not attached as
    /// the inner exception because its message is what is being cleaned.
    /// </summary>
    public static Exception Sanitize(Exception exception, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = Clean(exception.Message, secrets);
        return exception switch
        {
            GuestCredentialRejectedException => new GuestCredentialRejectedException(message),
            GuestUnavailableException => new GuestUnavailableException(message),
            GuestOperationException => new GuestOperationException(message),
            GuestAccountConflictException => new GuestAccountConflictException(message),
            _ => exception,
        };
    }

    public static string Clean(string message, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)).OrderByDescending(secret => secret!.Length))
        {
            message = message.Replace(secret!, Redacted, StringComparison.Ordinal);
        }

        var cleaned = new string(message.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return cleaned.Length <= MaxMessageLength ? cleaned : cleaned[..MaxMessageLength] + "...";
    }
}

/// <summary>Generates passwords that satisfy the default Windows complexity policy.</summary>
public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#%+-=?@^_";

    public static string Generate(int length = 24)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);

        // One character from each class, the rest from all classes, then shuffled.
        var all = Upper + Lower + Digits + Symbols;
        var chars = new List<char>
        {
            RandomNumberGenerator.GetItems<char>(Upper, 1)[0],
            RandomNumberGenerator.GetItems<char>(Lower, 1)[0],
            RandomNumberGenerator.GetItems<char>(Digits, 1)[0],
            RandomNumberGenerator.GetItems<char>(Symbols, 1)[0],
        };
        chars.AddRange(RandomNumberGenerator.GetItems<char>(all, length - chars.Count));

        var shuffled = chars.ToArray();
        RandomNumberGenerator.Shuffle<char>(shuffled);
        return new string(shuffled);
    }
}
