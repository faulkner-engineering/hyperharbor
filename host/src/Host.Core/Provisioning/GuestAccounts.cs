using System.Security.Cryptography;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>A credential for a guest account. ToString never includes the password.</summary>
public sealed record GuestCredential(string UserName, string Password)
{
    public override string ToString() => $"GuestCredential {{ UserName = {UserName} }}";
}

/// <param name="IsLocal">PrincipalSource is Local (not a Microsoft account or domain account).</param>
public sealed record GuestAccountState(bool Exists, bool IsLocal, bool Enabled, bool InRemoteDesktopUsers)
{
    public bool IsReadyForRemoteDesktop => Exists && IsLocal && Enabled && InRemoteDesktopUsers;
}

/// <summary>
/// Manages a User's local account inside a guest through PowerShell Direct. Every call
/// authenticates to the guest with the stored administrator credential.
/// </summary>
public interface IGuestAccountManager
{
    Task<GuestAccountState> InspectAsync(Guid vmId, GuestCredential admin, string accountName, CancellationToken cancellationToken);

    /// <summary>Creates the local account, or resets it when it already exists, and adds it to Remote Desktop Users.</summary>
    /// <exception cref="GuestAccountConflictException">An account with this name exists and is not local.</exception>
    Task ProvisionAsync(Guid vmId, GuestCredential admin, string accountName, string password, bool enableRemoteDesktop, CancellationToken cancellationToken);

    Task SetPasswordAsync(Guid vmId, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken);
}

/// <summary>The guest rejected the administrator credential (422).</summary>
public sealed class GuestCredentialRejectedException(string message) : Exception(message);

/// <summary>PowerShell Direct could not reach the guest: not running, still starting, or not a Windows guest (503).</summary>
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
