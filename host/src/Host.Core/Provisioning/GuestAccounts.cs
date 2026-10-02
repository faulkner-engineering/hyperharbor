using System.Security.Cryptography;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>A credential for a guest account. ToString never includes the password.</summary>
public sealed record GuestCredential(string UserName, string Password)
{
    public override string ToString() => $"GuestCredential {{ UserName = {UserName} }}";
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
