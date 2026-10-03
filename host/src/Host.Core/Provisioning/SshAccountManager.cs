using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// Manages the User's account in a Linux guest over SSH, authenticating with the stored
/// administrator credential (password authentication) and running a fixed script under sudo.
/// The script is passed base64-encoded on the command line and contains no secrets; the sudo
/// password and the account password travel on the command's standard input only. The guest's
/// SSH host key is pinned at provisioning and checked on every later connection. Remote Desktop
/// is provided by xrdp.
/// </summary>
public sealed class SshAccountManager : IGuestAccountManager
{
    public const int SshPort = 22;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Package installs (xrdp, and optionally a desktop) can take several minutes on a slow guest.</summary>
    private static readonly TimeSpan ProvisionTimeout = TimeSpan.FromMinutes(20);

    private const string ResultPrefix = "HH-RESULT ";
    private const string PasswordLinePrefix = "HH:";

    public async Task<GuestAccountState> InspectAsync(GuestTarget target, GuestCredential admin, string accountName, CancellationToken cancellationToken)
    {
        var result = await RunAsync(target, admin, ["inspect", accountName], accountPassword: null, CommandTimeout, cancellationToken);
        return ParseState(result.Values);
    }

    public async Task<GuestTarget> ProvisionAsync(
        GuestTarget target,
        GuestCredential admin,
        string accountName,
        string password,
        GuestProvisionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = await RunAsync(
            target,
            admin,
            ["provision", accountName, options.EnableRemoteDesktop ? "1" : "0", options.InstallDesktop ? "1" : "0"],
            password,
            ProvisionTimeout,
            cancellationToken);
        return target with { SshHostKey = result.HostKey };
    }

    public async Task SetPasswordAsync(GuestTarget target, GuestCredential admin, string accountName, string password, CancellationToken cancellationToken)
    {
        await RunAsync(target, admin, ["setPassword", accountName], password, CommandTimeout, cancellationToken);
    }

    private sealed record ScriptResult(IReadOnlyDictionary<string, string> Values, string HostKey);

    private static async Task<ScriptResult> RunAsync(
        GuestTarget target,
        GuestCredential admin,
        string[] arguments,
        string? accountPassword,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.Address))
        {
            throw new GuestAccountConflictException("The VM has not reported a network address yet. Wait for it to finish starting.");
        }

        if (accountPassword is not null && (accountPassword.Contains('\n') || accountPassword.Contains(':')))
        {
            throw new ArgumentException("The account password cannot contain a colon or a line break.", nameof(accountPassword));
        }

        var userName = StripLocalPrefix(admin.UserName);
        var connection = new ConnectionInfo(
            target.Address,
            SshPort,
            userName,
            new PasswordAuthenticationMethod(userName, admin.Password),
            new KeyboardInteractiveAuthenticationMethod(userName))
        {
            Timeout = ConnectTimeout,
        };
        foreach (var method in connection.AuthenticationMethods.OfType<KeyboardInteractiveAuthenticationMethod>())
        {
            method.AuthenticationPrompt += (_, prompt) =>
            {
                foreach (var item in prompt.Prompts)
                {
                    item.Response = admin.Password;
                }
            };
        }

        string? presentedKey = null;
        using var client = new SshClient(connection);
        client.HostKeyReceived += (_, e) =>
        {
            presentedKey = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = IsTrustedHostKey(target.SshHostKey, presentedKey);
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(deadline.Token);
        }
        catch (SshAuthenticationException ex)
        {
            throw new GuestCredentialRejectedException(
                $"The guest rejected the password for {userName} over SSH. Check the user name and password, and that the SSH server " +
                $"allows password authentication (PasswordAuthentication yes). {ex.Message}");
        }
        catch (SshConnectionException) when (presentedKey is not null && target.SshHostKey is not null && presentedKey != target.SshHostKey)
        {
            throw new GuestAccountConflictException(
                $"The VM's SSH host key changed (expected {target.SshHostKey}, got {presentedKey}). If the guest was reinstalled, " +
                "set it up again and choose to trust its new host key.");
        }
        catch (Exception ex) when (ex is SocketException or SshConnectionException or SshOperationTimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new GuestUnavailableException(
                $"Could not connect to SSH at {target.Address}:{SshPort}. Install and start the OpenSSH server in the guest. {ex.Message}".Trim());
        }

        try
        {
            var asRoot = string.Equals(userName, "root", StringComparison.Ordinal);
            if (!asRoot)
            {
                await CheckSudoAsync(client, userName, admin.Password, deadline.Token);
            }

            var stdin = new StringBuilder();
            if (!asRoot)
            {
                // Read by sudo, unless the account needs no password for sudo; the script skips it then.
                stdin.Append(admin.Password).Append('\n');
            }

            if (accountPassword is not null)
            {
                stdin.Append(PasswordLinePrefix).Append(accountPassword).Append('\n');
            }

            var output = await ExecuteAsync(client, CommandLine(asRoot, arguments), stdin.ToString(), deadline.Token, cancellationToken);
            return new ScriptResult(Interpret(output.Stdout, output.Stderr), presentedKey!);
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>Verifies sudo with the administrator password before any secret is sent to the script.</summary>
    private static async Task CheckSudoAsync(SshClient client, string userName, string password, CancellationToken cancellationToken)
    {
        var output = await ExecuteAsync(client, "sudo -S -k -p '' true", password + "\n", cancellationToken, cancellationToken);
        if (output.ExitStatus != 0)
        {
            throw new GuestCredentialRejectedException(
                $"{userName} signed in over SSH but could not use sudo. Use an account in the sudo (or wheel) group. {LastLines(output.Stderr)}".Trim());
        }
    }

    private sealed record CommandOutput(int? ExitStatus, string Stdout, string Stderr);

    private static async Task<CommandOutput> ExecuteAsync(
        SshClient client,
        string commandText,
        string stdin,
        CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand(commandText);
        var execution = command.ExecuteAsync(deadline);
        using (var input = command.CreateInputStream())
        {
            await input.WriteAsync(Encoding.UTF8.GetBytes(stdin), deadline);
        }

        try
        {
            await execution;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuestOperationException("The command in the guest did not finish in time.");
        }

        return new CommandOutput(command.ExitStatus, command.Result, command.Error);
    }

    /// <summary>
    /// Builds the remote command. The script is decoded by the admin's login shell and passed to
    /// bash -c, so nothing is written to disk in the guest.
    /// </summary>
    internal static string CommandLine(bool asRoot, IEnumerable<string> arguments)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Script.ReplaceLineEndings("\n")));
        var quoted = string.Join(' ', arguments.Select(Quote));
        var bash = $"bash -c \"$(printf %s {encoded} | base64 -d)\" hyperharbor {quoted}";
        return asRoot ? bash : $"sudo -S -k -p '' {bash}";
    }

    /// <summary>Trust on first use (nothing pinned); afterwards only the pinned key.</summary>
    internal static bool IsTrustedHostKey(string? pinned, string presented) =>
        pinned is null || string.Equals(pinned, presented, StringComparison.Ordinal);

    /// <summary>Quotes one argument for a POSIX shell.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>"Administrator" style names from the Windows form may arrive as ".\name".</summary>
    private static string StripLocalPrefix(string userName) =>
        userName.StartsWith(@".\", StringComparison.Ordinal) ? userName[2..] : userName.Trim();

    internal static GuestAccountState ParseState(IReadOnlyDictionary<string, string> values)
    {
        bool Flag(string key) => values.TryGetValue(key, out var value) && value == "1";
        return new GuestAccountState(Flag("exists"), Flag("local"), Flag("enabled"), Flag("rdp") && Flag("desktop"));
    }

    /// <summary>
    /// Parses the script's result line, "HH-RESULT ok key=value ..." or "HH-RESULT error stage message",
    /// into values or a typed exception.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Interpret(string stdout, string stderr)
    {
        var line = stdout.Split('\n', StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => candidate.StartsWith(ResultPrefix, StringComparison.Ordinal));
        if (line is null)
        {
            throw new GuestOperationException($"Unexpected output from the guest. {LastLines(stderr)}".Trim());
        }

        var parts = line[ResultPrefix.Length..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["ok", ..])
        {
            return parts.Skip(1)
                .SelectMany(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        }

        var stage = parts.Length > 1 ? parts[1] : "guest";
        var message = parts.Length > 2 ? parts[2] : "Unknown error.";
        throw stage switch
        {
            "notLocal" or "noDesktop" => new GuestAccountConflictException(message),
            _ => new GuestOperationException($"{message} {LastLines(stderr)}".Trim()),
        };
    }

    private static string LastLines(string text, int count = 3) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));

    /// <summary>
    /// Runs as root. Arguments: operation, account name, and for provision the enable-xrdp and
    /// install-desktop flags. The account password is the stdin line starting with "HH:"; any line
    /// before it (the sudo password, when sudo did not need it) is ignored. Prints one result line.
    /// Supports apt (Debian, Ubuntu) and dnf (Fedora, RHEL with EPEL) for package installs.
    /// </summary>
    private const string Script = """
        set -u
        export LC_ALL=C DEBIAN_FRONTEND=noninteractive
        op="${1:-}"; name="${2:-}"; enable_rdp="${3:-0}"; install_desktop="${4:-0}"
        password=""
        while IFS= read -r line; do
            case "$line" in HH:*) password="${line#HH:}"; break ;; esac
        done

        reply_error() { echo "HH-RESULT error $1 $2"; exit 0; }

        is_local() { grep -q "^${name}:" /etc/passwd; }
        has_desktop() { ls /usr/share/xsessions/*.desktop >/dev/null 2>&1; }

        install_packages() {
            if command -v apt-get >/dev/null 2>&1; then
                apt-get -o DPkg::Lock::Timeout=300 update -q >&2 || true
                apt-get -o DPkg::Lock::Timeout=300 install -y -q "$@" >&2 \
                    || reply_error guest "Installing $* with apt-get failed."
            elif command -v dnf >/dev/null 2>&1; then
                dnf install -y "$@" >&2 || reply_error guest "Installing $* with dnf failed."
            else
                reply_error guest "No supported package manager (apt-get or dnf). Install xrdp and a desktop manually."
            fi
        }

        install_desktop_packages() {
            if command -v apt-get >/dev/null 2>&1; then
                install_packages xfce4 xfce4-terminal dbus-x11
            else
                install_packages xfce4-session xfwm4 xfdesktop xfce4-panel xfce4-terminal dbus-x11
            fi
        }

        set_password() {
            [ -n "$password" ] || reply_error guest "No password was provided."
            printf '%s:%s\n' "$name" "$password" | chpasswd || reply_error guest "Setting the password with chpasswd failed."
        }

        inspect() {
            exists=0; is_local_account=0; enabled=0; rdp=0; desktop=0
            if getent passwd "$name" >/dev/null 2>&1; then
                exists=1
                if is_local; then is_local_account=1; fi
                case "$(passwd -S "$name" 2>/dev/null | awk '{print $2}')" in P|PS) enabled=1 ;; esac
            fi
            if systemctl is-active --quiet xrdp 2>/dev/null; then rdp=1; fi
            if has_desktop; then desktop=1; fi
            echo "HH-RESULT ok exists=$exists local=$is_local_account enabled=$enabled rdp=$rdp desktop=$desktop"
        }

        provision() {
            if getent passwd "$name" >/dev/null 2>&1; then
                is_local || reply_error notLocal "An account named $name exists but is not a local account."
            else
                useradd --create-home --shell /bin/bash --comment "HyperHarbor" "$name" \
                    || reply_error guest "Creating the account $name failed."
            fi
            set_password
            usermod --unlock "$name" >/dev/null 2>&1 || true
            chage --expiredate -1 --maxdays 99999 "$name" >/dev/null 2>&1 || true

            if [ "$enable_rdp" = 1 ]; then
                if ! has_desktop; then
                    [ "$install_desktop" = 1 ] || reply_error noDesktop \
                        "No desktop environment is installed in the VM. Remote Desktop on Linux needs one. Choose the option to install a lightweight desktop (Xfce), or install one yourself."
                    install_desktop_packages
                fi
                if ! command -v xrdp >/dev/null 2>&1 && [ ! -x /usr/sbin/xrdp ]; then
                    install_packages xrdp xorgxrdp
                fi
                if getent group ssl-cert >/dev/null 2>&1; then usermod -aG ssl-cert xrdp >/dev/null 2>&1 || true; fi
                systemctl enable --now xrdp >&2 || reply_error guest "Starting the xrdp service failed."
                if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
                    ufw allow 3389/tcp >&2 || true
                fi
                if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
                    firewall-cmd --permanent --add-port=3389/tcp >&2 && firewall-cmd --reload >&2 || true
                fi
            fi

            # Prefer Xfce for xrdp sessions when it is installed; GNOME sessions over xrdp are unreliable.
            home="$(getent passwd "$name" | cut -d: -f6)"
            if command -v xfce4-session >/dev/null 2>&1 && [ -n "$home" ] && [ ! -e "$home/.xsession" ]; then
                echo "xfce4-session" > "$home/.xsession"
                chown "$name:" "$home/.xsession"
            fi
            echo "HH-RESULT ok"
        }

        case "$op" in
            inspect) inspect ;;
            provision) provision ;;
            setPassword)
                is_local || reply_error notLocal "The account $name is missing or not local. Set the VM up again."
                set_password
                echo "HH-RESULT ok"
                ;;
            *) reply_error guest "Unknown operation $op." ;;
        esac
        """;
}
