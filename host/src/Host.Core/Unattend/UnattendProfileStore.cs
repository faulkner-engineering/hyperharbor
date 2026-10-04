using System.Text.Json;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>No profile with this ID exists for the User (404).</summary>
public sealed class UnattendProfileNotFoundException(string id)
    : Exception($"The unattended profile \"{id}\" was not found.");

/// <summary>
/// Unattended install profiles: three built-ins that cannot change, plus profiles each User creates.
/// A User sees the built-ins and their own profiles only. Profiles hold no secrets.
/// </summary>
public sealed partial class UnattendProfileStore
{
    public const string FileName = "unattend-profiles.json";
    public const string ProfileReadOnlyCode = "profileReadOnly";
    public const int MaxNameLength = 60;
    public const int MaxSshKeys = 10;
    public const int MaxPackages = 50;

    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.Options) { WriteIndented = true };

    private static readonly string[] ReservedAccountNames =
        ["administrator", "admin", "guest", "defaultaccount", "wdagutilityaccount", "root", "daemon", "nobody", "ubuntu"];

    private readonly string _path;
    private readonly Func<string> _hostTimeZone;
    private readonly object _gate = new();
    private List<StoredProfile>? _profiles;

    /// <param name="hostTimeZone">The host's Windows time zone ID; the default for profiles that leave it out.</param>
    public UnattendProfileStore(string dataDirectory, Func<string>? hostTimeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
        _hostTimeZone = hostTimeZone ?? (() => TimeZoneInfo.Local.Id);
    }

    /// <summary>The built-in profiles, with the host's time zone.</summary>
    public IReadOnlyList<UnattendProfile> BuiltIns()
    {
        var windowsZone = _hostTimeZone();
        var linuxZone = TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsZone, out var iana) ? iana : "Etc/UTC";
        return
        [
            new("windows-workstation", "Windows Workstation", true, InstallOs.Windows, UnattendProfile.DefaultAdminAccountName,
                windowsZone, UnattendProfile.DefaultLocale, new WindowsInstallSettings("Windows 11 Pro"), null),
            new("windows-burner", "Windows Burner", true, InstallOs.Windows, UnattendProfile.DefaultAdminAccountName,
                windowsZone, UnattendProfile.DefaultLocale, new WindowsInstallSettings("Windows 11 Pro", BypassHardwareChecks: true), null),
            new("ubuntu-dev-server", "Ubuntu Dev Server", true, InstallOs.Linux, UnattendProfile.DefaultAdminAccountName,
                linuxZone, UnattendProfile.DefaultLocale, null, new LinuxInstallSettings([], ["git", "build-essential", "curl"], InstallDesktop: true)),
        ];
    }

    /// <summary>The built-ins, then the User's own profiles by name.</summary>
    public IReadOnlyList<UnattendProfile> List(Guid userId)
    {
        lock (_gate)
        {
            return [.. BuiltIns(), .. Profiles().Where(stored => stored.OwnerUserId == userId).Select(stored => stored.Profile).OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <exception cref="UnattendProfileNotFoundException">The User has no profile with this ID.</exception>
    public UnattendProfile Get(Guid userId, string id) =>
        List(userId).FirstOrDefault(profile => profile.Id == id) ?? throw new UnattendProfileNotFoundException(id);

    /// <exception cref="LifecycleValidationException">The request is invalid.</exception>
    public UnattendProfile Create(Guid userId, UnattendProfileRequest request)
    {
        var profile = Normalize(Guid.NewGuid().ToString("D"), request);
        lock (_gate)
        {
            Profiles().Add(new StoredProfile(userId, profile));
            Save();
        }

        return profile;
    }

    /// <exception cref="UnattendProfileNotFoundException">The User has no profile with this ID.</exception>
    /// <exception cref="LifecycleConflictException">The profile is built in.</exception>
    /// <exception cref="LifecycleValidationException">The request is invalid.</exception>
    public UnattendProfile Update(Guid userId, string id, UnattendProfileRequest request)
    {
        lock (_gate)
        {
            var index = FindOwned(userId, id);
            var profile = Normalize(id, request);
            Profiles()[index] = new StoredProfile(userId, profile);
            Save();
            return profile;
        }
    }

    /// <exception cref="UnattendProfileNotFoundException">The User has no profile with this ID.</exception>
    /// <exception cref="LifecycleConflictException">The profile is built in.</exception>
    public void Delete(Guid userId, string id)
    {
        lock (_gate)
        {
            Profiles().RemoveAt(FindOwned(userId, id));
            Save();
        }
    }

    /// <summary>Validates a request and fills in defaults.</summary>
    /// <exception cref="LifecycleValidationException">The request is invalid; Errors lists each problem.</exception>
    public UnattendProfile Normalize(string id, UnattendProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ValidationIssue>();
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            errors.Add(new("name", $"Use a name of 1 to {MaxNameLength} characters."));
        }

        var linux = request.Os == InstallOs.Linux;
        var account = (request.AdminAccountName ?? string.Empty).Trim();
        if (!(linux ? LinuxAccountName() : WindowsAccountName()).IsMatch(account))
        {
            errors.Add(new("adminAccountName", linux
                ? "Use 1 to 32 lowercase letters, digits, hyphens, or underscores, starting with a letter."
                : "Use 1 to 20 letters, digits, periods, hyphens, or underscores, starting with a letter or digit."));
        }
        else if (ReservedAccountNames.Contains(account, StringComparer.OrdinalIgnoreCase)
            || account.StartsWith("hh-", StringComparison.OrdinalIgnoreCase)
            || account.StartsWith("hhc-", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new("adminAccountName", $"\"{account}\" is reserved. Choose another administrator name."));
        }

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? DefaultTimeZone(request.Os) : request.TimeZone.Trim();
        if (!IsTimeZone(timeZone, request.Os))
        {
            errors.Add(new("timeZone", linux
                ? "Use an IANA time zone, for example America/Chicago."
                : "Use a Windows time zone ID, for example Central Standard Time."));
        }

        var locale = (request.Locale ?? string.Empty).Trim();
        if (!LocaleTag().IsMatch(locale))
        {
            errors.Add(new("locale", "Use a language tag such as en-US."));
        }

        WindowsInstallSettings? windows = null;
        LinuxInstallSettings? linuxSettings = null;
        if (linux)
        {
            if (request.Windows is not null)
            {
                errors.Add(new("windows", "Leave the Windows settings out of a Linux profile."));
            }

            linuxSettings = NormalizeLinux(request.Linux ?? new LinuxInstallSettings(), errors);
        }
        else
        {
            if (request.Linux is not null)
            {
                errors.Add(new("linux", "Leave the Linux settings out of a Windows profile."));
            }

            if (request.Windows is null)
            {
                errors.Add(new("windows", "A Windows profile needs Windows settings."));
            }
            else
            {
                var edition = (request.Windows.DefaultEdition ?? string.Empty).Trim();
                if (edition.Length is 0 or > 100 || edition.Any(char.IsControl))
                {
                    errors.Add(new("windows.defaultEdition", "Use the edition name from the ISO, for example Windows 11 Pro."));
                }

                windows = request.Windows with { DefaultEdition = edition };
            }
        }

        if (errors.Count > 0)
        {
            throw new LifecycleValidationException(errors[0].Message, errors);
        }

        return new UnattendProfile(id, name, false, request.Os, account, timeZone, locale, windows, linuxSettings);
    }

    private static LinuxInstallSettings NormalizeLinux(LinuxInstallSettings settings, List<ValidationIssue> errors)
    {
        var keys = (settings.SshAuthorizedKeys ?? []).Select(key => key.Trim()).Where(key => key.Length > 0).ToList();
        if (keys.Count > MaxSshKeys)
        {
            errors.Add(new("linux.sshAuthorizedKeys", $"Use at most {MaxSshKeys} keys."));
        }
        else if (keys.FirstOrDefault(key => !SshPublicKey().IsMatch(key)) is { } badKey)
        {
            errors.Add(new("linux.sshAuthorizedKeys", $"\"{Shorten(badKey)}\" is not an OpenSSH public key (ssh-ed25519, ecdsa-sha2-nistp*, or ssh-rsa)."));
        }

        var packages = (settings.Packages ?? []).Select(package => package.Trim()).Where(package => package.Length > 0).Distinct().ToList();
        if (packages.Count > MaxPackages)
        {
            errors.Add(new("linux.packages", $"Use at most {MaxPackages} packages."));
        }
        else if (packages.FirstOrDefault(package => !PackageName().IsMatch(package)) is { } badPackage)
        {
            errors.Add(new("linux.packages", $"\"{Shorten(badPackage)}\" is not a valid package name."));
        }

        return settings with { SshAuthorizedKeys = keys, Packages = packages };
    }

    private string DefaultTimeZone(InstallOs os)
    {
        var windowsZone = _hostTimeZone();
        return os == InstallOs.Windows
            ? windowsZone
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsZone, out var iana) ? iana : "Etc/UTC";
    }

    private static bool IsTimeZone(string timeZone, InstallOs os)
    {
        if (timeZone.Length > 64 || timeZone.Any(char.IsControl))
        {
            return false;
        }

        if (os == InstallOs.Linux)
        {
            return timeZone == "Etc/UTC" || TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _);
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone, out _);
    }

    private int FindOwned(Guid userId, string id)
    {
        if (BuiltIns().Any(profile => profile.Id == id))
        {
            throw new LifecycleConflictException("Built-in profiles cannot be changed. Copy it to make your own.", ProfileReadOnlyCode);
        }

        var index = Profiles().FindIndex(stored => stored.OwnerUserId == userId && stored.Profile.Id == id);
        return index >= 0 ? index : throw new UnattendProfileNotFoundException(id);
    }

    private List<StoredProfile> Profiles()
    {
        if (_profiles is null)
        {
            _profiles = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<StoredProfile>>(File.ReadAllText(_path), JsonOptions)
                    ?? throw new InvalidDataException($"The profile file '{_path}' is invalid.")
                : [];
        }

        return _profiles;
    }

    private void Save() => ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(Profiles(), JsonOptions));

    private static string Shorten(string value) => value.Length <= 40 ? value : value[..40] + "…";

    private sealed record StoredProfile(Guid OwnerUserId, UnattendProfile Profile);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$")]
    private static partial Regex WindowsAccountName();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex LinuxAccountName();

    [GeneratedRegex("^[a-z]{2,3}-[A-Z]{2}$")]
    private static partial Regex LocaleTag();

    [GeneratedRegex("^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\\.com) [A-Za-z0-9+/]+={0,3}( [\\x20-\\x7E]{0,200})?$")]
    private static partial Regex SshPublicKey();

    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]{0,62}$")]
    private static partial Regex PackageName();
}
