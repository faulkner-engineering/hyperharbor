using System.Text;
using System.Text.Json;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Users;

/// <summary>
/// A person who owns paired devices. Each User has one local account on every provisioned VM.
/// </summary>
/// <param name="VmAccountName">Local VM account name, for example hh-owner.</param>
public sealed record User(Guid UserId, string Name, string VmAccountName, DateTimeOffset CreatedAt);

/// <summary>
/// Persists Users. The MVP creates a single default User on first run, but callers must not assume
/// there is only one: devices and VM accounts always reference a specific UserId.
/// </summary>
public sealed class UserStore
{
    public const string DefaultUserName = "owner";
    private const string FileName = "users.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private List<User>? _users;

    public UserStore(string dataDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<User> List()
    {
        lock (_gate)
        {
            return Users().ToList();
        }
    }

    public User? Find(Guid userId)
    {
        lock (_gate)
        {
            return Users().FirstOrDefault(user => user.UserId == userId);
        }
    }

    /// <summary>
    /// The User that new pairings belong to. Created on first use. With more than one User this
    /// becomes the oldest one until user selection exists.
    /// </summary>
    public User GetOrCreateDefault()
    {
        lock (_gate)
        {
            var users = Users();
            if (users.Count > 0)
            {
                return users.OrderBy(user => user.CreatedAt).First();
            }

            var user = new User(Guid.NewGuid(), DefaultUserName, VmAccountName.For(DefaultUserName), _time.GetUtcNow());
            users.Add(user);
            ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(users, JsonOptions));
            return user;
        }
    }

    private List<User> Users()
    {
        if (_users is null)
        {
            _users = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<User>>(File.ReadAllText(_path), JsonOptions)
                    ?? throw new InvalidDataException($"The user file '{_path}' is invalid.")
                : [];
        }

        return _users;
    }
}

/// <summary>
/// Builds local VM account names: "hh-" plus the user name, limited to a-z, 0-9, and hyphen, and to
/// the 20-character limit of Windows local account names.
/// </summary>
public static class VmAccountName
{
    public const string Prefix = "hh-";
    public const int MaxLength = 20;

    public static string For(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var cleaned = new StringBuilder();
        foreach (var c in userName.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                cleaned.Append(c);
            }
        }

        var body = cleaned.ToString().Trim('-');
        if (body.Length == 0)
        {
            throw new ArgumentException("The user name has no characters that are valid in an account name.", nameof(userName));
        }

        var name = Prefix + body;
        return name.Length > MaxLength ? name[..MaxLength].TrimEnd('-') : name;
    }
}
