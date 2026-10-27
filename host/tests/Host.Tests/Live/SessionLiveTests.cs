using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// The checks that need a real signed-in session in a Windows guest, which used to be done by hand before a release:
/// <list type="number">
/// <item>Data the user protected with DPAPI (and Brave's key, which is DPAPI data) still opens after a reconnect and
/// after a VM restart, each with a rotated password (Brave turned extensions off when a reset lost that data).</item>
/// <item>The User's hha- account is ready for User Account Control prompts after a Connect, and an administrator
/// window's passphrase signs in while it is open and not after it ends.</item>
/// <item>Web links open in Brave for the signed-in user (the default associations policy applied at sign-in).</item>
/// <item>Edge does not start on its own at sign-in.</item>
/// <item>The session window is titled "&lt;VM name&gt; - &lt;address&gt; - Remote Desktop Connection".</item>
/// <item>A windowed session follows the window's size (dynamic resolution); a minimal .rdp file is measured the same
/// way for comparison.</item>
/// </list>
/// Sessions are opened by the client's own code: the Rust agent in client/src-tauri/src/session_live.rs runs
/// <c>rdp::launch</c> for each request this test writes to HH_SESSION_DIR. scripts\live-session.ps1 starts both.
/// Scripts run in the user's session through a scheduled task with an interactive principal, so they see what the
/// signed-in user sees. Every check runs even when an earlier one fails, and the test fails at the end with all of them.
/// <para>
/// Needs an active desktop on this machine (mstsc draws and resizes only there), RDP trust set up for this account in
/// the client, and the VM made by <see cref="Release024LiveTests"/>'s profile (Brave as the default browser, Edge
/// kept out of the way): HH_LIVE_GUEST_VM=&lt;id&gt; reuses one; without it, HH_SPIKE_ISO creates HyperHarbor-Test
/// (about 25 minutes) and deletes it afterwards unless HH_MACHINE_KEEP=1. About 10 minutes with an existing VM.
/// </para>
/// </summary>
public sealed class SessionLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Extra DPAPI entropy for launch requests; the same as ENTROPY in session_live.rs.</summary>
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("HyperHarbor session live test");

    private readonly List<string> _failures = [];
    private readonly List<int> _sessions = [];
    private string _folder = "";
    private int _requests;

    [EnvironmentFact("HH_SESSION_LIVE", "HH_SESSION_DIR")]
    public async Task Live_SignedInSessionChecks()
    {
        _folder = Directory.CreateDirectory(Environment.GetEnvironmentVariable("HH_SESSION_DIR")!).FullName;
        var progress = Path.Combine(Path.GetTempPath(), "hyperharbor-session-progress.log");
        void Log(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
            output.WriteLine(stamped);
            File.AppendAllText(progress, stamped + Environment.NewLine);
        }

        // Window sizes in physical pixels, whatever this monitor's scale (per monitor v2, for the whole test process).
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var agent = await WaitForFileAsync(Path.Combine(_folder, "agent.json"), TimeSpan.FromMinutes(10))
            ?? throw new InvalidOperationException($"The client agent did not start; scripts\\live-session.ps1 starts it with {_folder}.");
        Log("Agent: " + agent.ToJsonString());
        if ((string?)agent["trust"]?["state"] != "ready")
        {
            Log("RDP trust is not set up for this account, so mstsc shows its security dialog and the run stops there. Set it up in the client first.");
        }

        await using var host = await LiveHostHarness.StartAsync(Log);
        var reused = Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM") is { Length: > 0 } existing;
        var vmId = reused
            ? Guid.Parse(Environment.GetEnvironmentVariable("HH_LIVE_GUEST_VM")!)
            : Environment.GetEnvironmentVariable("HH_SPIKE_ISO") is { Length: > 0 }
                ? await Release024LiveTests.CreateAsync(host, Log)
                : throw new InvalidOperationException("Set HH_LIVE_GUEST_VM to a VM made from the release profile, or HH_SPIKE_ISO to create one.");
        string? address = null;
        try
        {
            await MachineProfileLiveTests.WaitUntilReadyAsync(host, vmId, Log);
            var vmName = (string)(await host.GetJsonAsync($"/api/v1/vms/{vmId}"))["name"]!;
            var admin = new VmCredentialStore(host.DataDirectory).Find(vmId)!;
            var guest = new Guest(vmId, admin);

            // A. The first session: its window title, then the checks of a fresh sign-in.
            var first = await ConnectAsync(host, vmId, Log);
            address = (string)first["address"]!;
            var user = ((string)first["userName"]!).TrimStart('.', '\\');
            var pid = await LaunchAsync(first, vmName, plain: false, Log);
            var window = await WaitForSessionWindowAsync(pid, address, Log);
            await CheckAsync("5. Session window title", Log, () =>
            {
                Assert.Equal($"{vmName} - {address} - Remote Desktop Connection", WindowTitle(window));
                return Task.CompletedTask;
            });
            var signedIn = await WaitForSignInAsync(guest, user, Log);

            await CheckAsync("3. Web links open in Brave", Log, async () =>
            {
                var result = await guest.AsUserAsync(user, DefaultBrowserScript);
                Log("Default browser: " + result.ToJsonString());
                Log("User Choice Protection: " + (await guest.AsAdminAsync(UcpdScript)).ToJsonString());
                Assert.Equal("BraveHTML", (string?)result["http"]);
                Assert.Equal("BraveHTML", (string?)result["https"]);
            });

            await CheckAsync("4. Edge does not start on its own", Log, async () =>
            {
                // Startup boost and first-run tasks start within the first minute of a sign-in.
                var settle = signedIn + TimeSpan.FromSeconds(90) - DateTime.UtcNow;
                if (settle > TimeSpan.Zero)
                {
                    await Task.Delay(settle);
                }

                var result = await guest.AsAdminAsync(EdgeProcessesScript, user);
                Log("Edge: " + result.ToJsonString());
                Assert.Equal(0, (int)result["count"]!);
            });

            JsonNode? protectedData = null;
            await CheckAsync("1a. DPAPI data and Brave's key open in the first session", Log, async () =>
            {
                protectedData = await guest.AsUserAsync(user, ProtectScript, TimeSpan.FromMinutes(2));
                Log("Protected: " + Describe(protectedData));
                Assert.Equal("ok", (string?)protectedData["brave"]);
            });

            await CheckAsync("2. hha account ready for UAC, passphrase only while the window is open", Log, async () =>
            {
                var accountName = (string)(await host.GetJsonAsync($"/api/v1/vms/{vmId}/guest-admin"))["accountName"]!;
                await AssertReadyForUacAsync(guest, accountName, Log);
                var opened = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/guest-admin");
                Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
                var passphrase = (string)(await opened.Content.ReadFromJsonAsync<JsonObject>())!["passphrase"]!;
                var during = await guest.AsAdminAsync(LogonScript, accountName, passphrase);
                Log($"Sign-in with the passphrase while open: {during.ToJsonString()}");
                Assert.True((bool)during["ok"]!);
                var closed = await host.SendElevatedAsync(HttpMethod.Delete, $"/api/v1/vms/{vmId}/guest-admin");
                Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());
                var after = await guest.AsAdminAsync(LogonScript, accountName, passphrase);
                Log($"Sign-in with the passphrase after closing: {after.ToJsonString()}");
                Assert.False((bool)after["ok"]!);
                await AssertReadyForUacAsync(guest, accountName, Log);
            });

            await CheckAsync("6. A windowed session follows the window's size", Log, async () =>
            {
                var sizes = await MeasureResizeAsync(guest, user, window, Log);
                CloseSessions();

                // The same experiment with a minimal file, to tell the client's settings from Remote Desktop's own.
                try
                {
                    var plain = await WaitForSessionWindowAsync(await LaunchAsync(first, vmName, plain: true, Log), address, Log);
                    await WaitForUserScriptsAsync(guest, user);
                    var plainSizes = await MeasureResizeAsync(guest, user, plain, Log);
                    Log($"Minimal .rdp file: guest followed the window: {plainSizes.All(size => size.Follows)}.");
                }
                catch (Exception ex)
                {
                    Log($"The minimal .rdp comparison did not run: {ex.Message}");
                }
                finally
                {
                    CloseSessions();
                }

                Assert.All(sizes, size => Assert.True(size.Follows, $"window client {size.Client}, guest {size.Guest}"));
            });

            // B. Reconnect after the reuse window, so the host rotates the password (a change, not a reset).
            await CheckAsync("1b. DPAPI data and Brave's key open after a reconnect", Log, async () =>
            {
                Assert.NotNull(protectedData);
                CloseSessions();
                await ReconnectAsync(host, vmId, vmName, first, guest, user, Log);
                await AssertStillOpensAsync(guest, user, protectedData!, Log);
            });

            // C. After a restart, the most common case: Brave's extensions were off after each one.
            await CheckAsync("1c. DPAPI data and Brave's key open after a VM restart", Log, async () =>
            {
                Assert.NotNull(protectedData);
                CloseSessions();
                Log("Restarting the VM.");
                var restart = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/actions", new { action = "restart" });
                Assert.True(restart.IsSuccessStatusCode, await restart.Content.ReadAsStringAsync());
                await Task.Delay(TimeSpan.FromSeconds(30));
                await guest.WhenAnswersAsync();
                await ReconnectAsync(host, vmId, vmName, first, guest, user, Log);
                await AssertStillOpensAsync(guest, user, protectedData!, Log);
            });
        }
        finally
        {
            CloseSessions();
            await File.WriteAllTextAsync(Path.Combine(_folder, "done"), "");
            if (!reused && Environment.GetEnvironmentVariable("HH_MACHINE_KEEP") != "1")
            {
                await MachineProfileLiveTests.DeleteAsync(host, vmId, Log);
            }
        }

        Assert.True(_failures.Count == 0, "Failed checks:" + Environment.NewLine + string.Join(Environment.NewLine, _failures));
    }

    private async Task CheckAsync(string name, Action<string> log, Func<Task> check)
    {
        log($"--- {name}");
        try
        {
            await check();
            log($"PASS {name}");
        }
        catch (Exception ex)
        {
            log($"FAIL {name}: {ex.Message}");
            _failures.Add($"{name}: {ex.Message}");
        }
    }

    private static async Task<JsonObject> ConnectAsync(LiveHostHarness host, Guid vmId, Action<string> log)
    {
        var response = await host.SendElevatedAsync(HttpMethod.Post, $"/api/v1/vms/{vmId}/connect");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var connection = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        log($"Connect: {connection["userName"]}@{connection["address"]}:{connection["port"]}, reuse window until {connection["expiresAt"]}.");

        // Windowed, as the resize check needs; the rest is the VM's own defaults.
        var defaults = connection["defaults"] as JsonObject ?? [];
        defaults["window"] = "windowed";
        connection["defaults"] = defaults.DeepClone();
        return connection;
    }

    /// <summary>Waits out the reuse window so Connect rotates the password, then opens a session and waits for the desktop.</summary>
    private async Task ReconnectAsync(LiveHostHarness host, Guid vmId, string vmName, JsonObject previous, Guest guest, string user, Action<string> log)
    {
        var reuseEnds = DateTimeOffset.Parse((string)previous["expiresAt"]!) + TimeSpan.FromSeconds(5);
        if (reuseEnds > DateTimeOffset.UtcNow)
        {
            await Task.Delay(reuseEnds - DateTimeOffset.UtcNow);
        }

        var next = await ConnectAsync(host, vmId, log);
        Assert.NotEqual((string)previous["password"]!, (string)next["password"]!);
        previous["password"] = (string)next["password"]!;
        previous["expiresAt"] = (string)next["expiresAt"]!;
        await WaitForSessionWindowAsync(await LaunchAsync(next, vmName, plain: false, log), (string)next["address"]!, log);
        await WaitForSignInAsync(guest, user, log);
    }

    private static async Task AssertStillOpensAsync(Guest guest, string user, JsonNode protectedData, Action<string> log)
    {
        var result = await guest.AsUserAsync(user, UnprotectScript, TimeSpan.FromMinutes(2), (string)protectedData["blob"]!, (string)protectedData["text"]!);
        log("Opened: " + result.ToJsonString());
        Assert.Equal("ok", (string?)result["data"]);
        Assert.Equal("ok", (string?)result["brave"]);
    }

    private static async Task AssertReadyForUacAsync(Guest guest, string accountName, Action<string> log)
    {
        // Connect makes the account ready in the background, so give it a moment.
        JsonNode state = new JsonObject();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            state = await guest.AsAdminAsync(UacAccountScript, accountName);
            if ((bool)state["exists"]! && (bool)state["enabled"]! && (bool)state["administrator"]! && !(bool)state["hidden"]!)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        log($"{accountName}: {state.ToJsonString()}");
        Assert.True((bool)state["exists"]!, "the account does not exist");
        Assert.True((bool)state["enabled"]!, "the account is disabled");
        Assert.True((bool)state["administrator"]!, "the account is not in Administrators");
        Assert.False((bool)state["hidden"]!, "the account is hidden from sign-in screens");
    }

    private sealed record Size(string Client, string Guest, bool Follows);

    /// <summary>
    /// Resizes the session window larger, larger again, then smaller than the session's first resolution, and reads the
    /// guest's resolution each time. A size counts as followed only when the window took it (within its frame) and the
    /// guest matches the window: mstsc held a window at the session's 800x600 when asked for more (2026-10-27), which
    /// matched the guest without anything having followed.
    /// </summary>
    private static async Task<List<Size>> MeasureResizeAsync(Guest guest, string user, IntPtr window, Action<string> log)
    {
        ShowWindow(window, 9);
        var sizes = new List<Size>();
        foreach (var (width, height) in new[] { (1100, 760), (1400, 940), (700, 520) })
        {
            // As a drag of the window's edge: mstsc asks the guest for a new resolution when a move or size ends
            // (WM_EXITSIZEMOVE), which SetWindowPos alone never sends.
            SendMessage(window, 0x0231, IntPtr.Zero, IntPtr.Zero);
            Assert.True(SetWindowPos(window, IntPtr.Zero, 40, 40, width, height, 0x0014), "SetWindowPos failed.");
            SendMessage(window, 0x0232, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(TimeSpan.FromSeconds(1));
            GetWindowRect(window, out var frame);
            GetClientRect(window, out var client);
            log($"Asked for {width}x{height}; the window is {frame.Right - frame.Left}x{frame.Bottom - frame.Top}.");
            var clientSize = (client.Right - client.Left, client.Bottom - client.Top);
            // The frame adds at most a few dozen pixels to the client area.
            var took = Math.Abs(frame.Right - frame.Left - width) <= 40 && Math.Abs(frame.Bottom - frame.Top - height) <= 60;
            JsonNode resolution = new JsonObject();
            var follows = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!follows && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                resolution = await guest.AsUserAsync(user, ResolutionScript);
                follows = Math.Abs((int)resolution["width"]! - clientSize.Item1) <= 24 && Math.Abs((int)resolution["height"]! - clientSize.Item2) <= 24;
            }

            follows &= took;
            var size = new Size($"{clientSize.Item1}x{clientSize.Item2}", $"{resolution["width"]}x{resolution["height"]}", follows);
            log($"Window client {size.Client}{(took ? "" : $" (not the {width}x{height} asked for)")}, guest {size.Guest} in session {resolution["session"]}: {(follows ? "follows" : "does not follow")}.");
            sizes.Add(size);
        }

        return sizes;
    }

    private async Task<int> LaunchAsync(JsonObject connection, string vmName, bool plain, Action<string> log)
    {
        var id = $"{Environment.ProcessId}-{++_requests}";
        // The password crosses only protected with DPAPI for this account (the agent opens it), never in plain text.
        var safe = (JsonObject)connection.DeepClone();
        var password = Encoding.UTF8.GetBytes((string)safe["password"]!);
        safe.Remove("password");
        var request = new JsonObject
        {
            ["connection"] = safe,
            ["protectedPassword"] = Convert.ToBase64String(ProtectedData.Protect(password, PasswordEntropy, DataProtectionScope.CurrentUser)),
            ["vmName"] = vmName,
            ["plain"] = plain,
        };
        CryptographicOperations.ZeroMemory(password);
        var partial = Path.Combine(_folder, id + ".partial");
        await File.WriteAllTextAsync(partial, request.ToJsonString());
        File.Move(partial, Path.Combine(_folder, id + ".launch.json"));
        var answer = await WaitForFileAsync(Path.Combine(_folder, id + ".launched.json"), TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("The client agent did not answer the launch request.");
        if (answer["error"] is { } error)
        {
            throw new InvalidOperationException($"The client could not launch the session: {error}");
        }

        var pid = (int)answer["pid"]!;
        _sessions.Add(pid);
        log($"Launched mstsc {pid}{(plain ? " with a minimal .rdp file" : "")}.");
        return pid;
    }

    private void CloseSessions()
    {
        foreach (var pid in _sessions)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                process.WaitForExit(10_000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        _sessions.Clear();
    }

    /// <summary>The session window: a visible mstsc window whose title names the address, as it does once connected.</summary>
    private static async Task<IntPtr> WaitForSessionWindowAsync(int pid, string address, Action<string> log)
    {
        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Windows(pid).FirstOrDefault(window => WindowTitle(window).Contains(address, StringComparison.Ordinal)) is var found && found != IntPtr.Zero)
            {
                log($"Session window: \"{WindowTitle(found)}\".");
                return found;
            }

            AcceptCertificateWarning(pid, log);
            await Task.Delay(500);
        }

        // A dialog's message is in its child windows' text.
        var titles = string.Join(", ", Windows(pid).Select(window => $"\"{WindowTitle(window)}\" [{string.Join(" | ", ChildTexts(window))}]"));
        throw new InvalidOperationException($"mstsc {pid} did not open the session within {LaunchTimeout}; its windows: {titles}. A dialog may be waiting.");
    }

    /// <summary>Waits until the user's desktop runs (explorer) and scripts can run in the session.</summary>
    private static async Task<DateTime> WaitForSignInAsync(Guest guest, string user, Action<string> log)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(6);
        while (DateTime.UtcNow < deadline)
        {
            if ((int)(await guest.AsAdminAsync(ExplorerScript, user))["count"]! > 0)
            {
                var signedIn = DateTime.UtcNow;
                await WaitForUserScriptsAsync(guest, user);
                log($"{user} is signed in.");
                return signedIn;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        throw new InvalidOperationException($"{user} did not reach the desktop within 6 minutes.");
    }

    private static async Task WaitForUserScriptsAsync(Guest guest, string user)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            try
            {
                await guest.AsUserAsync(user, "@{ ready = $true }", TimeSpan.FromSeconds(30));
                return;
            }
            catch (InvalidOperationException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task<JsonNode?> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path);
                File.Delete(path);
                return JsonNode.Parse(text);
            }

            await Task.Delay(250);
        }

        return null;
    }

    /// <summary>Describes the protected-data result without the blob.</summary>
    private static string Describe(JsonNode result) => $"brave {result["brave"]}, blob of {((string?)result["blob"])?.Length ?? 0} characters";

    /// <summary>Runs scripts in the guest over PowerShell Direct as the stored administrator, or in a user's session.</summary>
    internal sealed class Guest(Guid vmId, GuestCredential admin)
    {
        public async Task<JsonNode> AsAdminAsync(string command, params object[] arguments) =>
            await RunAsync(command, arguments, TimeSpan.FromMinutes(3));

        /// <summary>
        /// Runs <paramref name="script"/> in <paramref name="user"/>'s signed-in session (a scheduled task with an
        /// interactive principal) and returns its value as JSON. Arguments reach the script as $args.
        /// </summary>
        public async Task<JsonNode> AsUserAsync(string user, string script, TimeSpan? timeout = null, params object[] arguments)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(60);
            var answer = await RunAsync(UserTaskScript, [user, script, (int)limit.TotalSeconds, arguments], limit + TimeSpan.FromMinutes(1));
            // The runner's own output can come before the JSON text; that text is always last.
            // Remoting adds PSComputerName to the string, so ConvertTo-Json writes it as { value, PSComputerName, ... }.
            var last = answer is JsonArray lines ? lines.LastOrDefault() : answer;
            var text = (last is JsonObject wrapped ? wrapped["value"] : last) is JsonValue value && value.TryGetValue<string>(out var line)
                ? line
                : throw new InvalidOperationException($"The user script runner answered {answer.ToJsonString()}.");
            var reply = JsonNode.Parse(text) ?? throw new InvalidOperationException("The user script answered nothing.");
            return (bool?)reply["ok"] == true
                ? reply["result"] ?? new JsonObject()
                : throw new InvalidOperationException($"The user script failed: {reply["error"]}");
        }

        /// <summary>Waits until PowerShell Direct answers, for example after a restart.</summary>
        public async Task WhenAnswersAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(6);
            while (true)
            {
                try
                {
                    await AsAdminAsync("@{ ready = $true }");
                    return;
                }
                catch (Exception ex) when (ex is GuestUnavailableException or GuestOperationException && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }

        /// <summary>
        /// Retries when the session does not open: during a user's first sign-in, PowerShell Direct refused the stored
        /// administrator for a while ("The credential is invalid", or "A remote session might have ended"), then
        /// accepted it again (2026-10-27).
        /// </summary>
        private async Task<JsonNode> RunAsync(string command, object[] arguments, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (true)
            {
                try
                {
                    return await PowerShellDirectRunner.RunAsync(
                        PowerShellDirectRunner.Encode(RunnerScript),
                        new { vmId, admin.UserName, admin.Password, command, arguments },
                        timeout,
                        CancellationToken.None) ?? new JsonObject();
                }
                catch (Exception ex) when (ex is GuestUnavailableException or GuestOperationException
                    && ex.Message.Contains("New-PSSession", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }
    }

    /// <summary>Runs `command` in the guest with `arguments` and answers its value.</summary>
    private const string RunnerScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        try {
            $password = ConvertTo-SecureString $request.Password -AsPlainText -Force
            $credential = New-Object System.Management.Automation.PSCredential($request.UserName, $password)
            $session = New-PSSession -VMId $request.vmId -Credential $credential
            try {
                $value = Invoke-Command -Session $session -ScriptBlock ([scriptblock]::Create($request.command)) -ArgumentList @($request.arguments)
            }
            finally {
                Remove-PSSession $session
            }
            @{ ok = $true; result = $value } | ConvertTo-Json -Compress -Depth 8
        }
        catch {
            @{ ok = $false; stage = 'run'; error = ($_ | Out-String) } | ConvertTo-Json -Compress
        }
        """;

    /// <summary>
    /// Runs in the guest as the administrator: writes the script to a file, runs it as the user through a scheduled
    /// task with an interactive principal (only while the user is signed in, in their session), and answers the JSON the
    /// script wrote, as text.
    /// </summary>
    private const string UserTaskScript = """
        param($user, $script, $seconds, $scriptArguments)
        $folder = 'C:\ProgramData\HyperHarborLive'
        New-Item -ItemType Directory -Force $folder | Out-Null
        icacls $folder /grant "${user}:(OI)(CI)M" | Out-Null
        $id = [guid]::NewGuid().ToString('N')
        $file = Join-Path $folder "$id.ps1"
        $out = Join-Path $folder "$id.json"
        $argumentsFile = Join-Path $folder "$id.args.json"
        ConvertTo-Json -InputObject @($scriptArguments) -Compress -Depth 4 | Set-Content -LiteralPath $argumentsFile -Encoding UTF8
        # Windows PowerShell's ConvertFrom-Json writes an array as one object, so it is cast rather than wrapped in @().
        $head = '$ErrorActionPreference = ''Stop''' + "`r`n" + '$out = $args[0]; $arguments = [object[]](Get-Content -LiteralPath $args[1] -Raw | ConvertFrom-Json)' + "`r`n" + 'try { $value = & {' + "`r`n"
        $tail = "`r`n" + '} @arguments; @{ ok = $true; result = $value } | ConvertTo-Json -Compress -Depth 8 | Set-Content -LiteralPath $out -Encoding UTF8 }' + "`r`n" + 'catch { @{ ok = $false; error = ($_ | Out-String) } | ConvertTo-Json -Compress | Set-Content -LiteralPath $out -Encoding UTF8 }'
        Set-Content -LiteralPath $file -Value ($head + $script + $tail) -Encoding UTF8
        $name = "HyperHarborLive-$id"
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$file`" `"$out`" `"$argumentsFile`""
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Force | Out-Null
        try {
            Start-ScheduledTask -TaskName $name
            $deadline = (Get-Date).AddSeconds($seconds)
            $text = $null
            while ($null -eq $text -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
                if (Test-Path -LiteralPath $out) {
                    # Not Get-Content: its string carries file properties that ConvertTo-Json writes out too.
                    try { $text = [IO.File]::ReadAllText($out) } catch { $text = $null }
                    if ($text -and -not $text.TrimEnd().EndsWith('}')) { $text = $null }
                }
            }
        }
        finally {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $file, $out, $argumentsFile -Force -ErrorAction SilentlyContinue
        }
        if ($null -eq $text) { '{"ok":false,"error":"The script did not finish in the user''s session; is the user signed in?"}' } else { $text }
        """;

    private const string ExplorerScript = """
        param($user)
        @{ count = @(Get-Process explorer -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { $_.UserName -like "*\$user" }).Count }
        """;

    private const string EdgeProcessesScript = """
        param($user)
        $edge = @(Get-Process msedge -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { $_.UserName -like "*\$user" })
        @{ count = $edge.Count; commandLines = @($edge | ForEach-Object { (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine } | Select-Object -First 3) }
        """;

    private const string UacAccountScript = """
        param($name)
        $account = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
        $administrators = @(Get-LocalGroupMember -SID S-1-5-32-544 | ForEach-Object { ($_.Name -split '\\')[-1] })
        $list = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList' -ErrorAction SilentlyContinue
        @{
            exists = $null -ne $account
            enabled = [bool]$account.Enabled
            administrator = $administrators -contains $name
            hidden = $null -ne $list -and $null -ne $list.$name -and $list.$name -eq 0
        }
        """;

    /// <summary>An interactive sign-in check with LogonUser; the passphrase arrives as an argument, never on a command line.</summary>
    private const string LogonScript = """
        param($name, $secret)
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class HyperHarborLogon {
            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool LogonUser(string user, string domain, string password, int type, int provider, out IntPtr token);
            [DllImport("kernel32.dll")]
            public static extern bool CloseHandle(IntPtr handle);
        }
        '@
        $token = [IntPtr]::Zero
        $ok = [HyperHarborLogon]::LogonUser($name, '.', $secret, 2, 0, [ref]$token)
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($ok) { [HyperHarborLogon]::CloseHandle($token) | Out-Null }
        @{ ok = $ok; error = if ($ok) { 0 } else { $code } }
        """;

    /// <summary>
    /// The User Choice Protection Driver, which stops processes other than Windows' own settings from writing the http
    /// and https choices, and the state of its scheduled task (which turns it back on).
    /// </summary>
    private const string UcpdScript = """
        $service = Get-Service UCPD -ErrorAction SilentlyContinue
        $task = Get-ScheduledTask -TaskPath '\Microsoft\Windows\AppxDeploymentClient\' -TaskName 'UCPD velocity' -ErrorAction SilentlyContinue
        @{ service = [string]$service.Status; startType = [string]$service.StartType; task = [string]$task.State }
        """;

    /// <summary>The ProgId that opens http and https links for the signed-in user, as Windows resolves it.</summary>
    private const string DefaultBrowserScript = """
        Add-Type -TypeDefinition @'
        using System.Runtime.InteropServices;
        using System.Text;
        public static class HyperHarborAssoc {
            [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
            public static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder output, ref int length);
        }
        '@
        function ProgId($protocol) {
            $buffer = New-Object System.Text.StringBuilder 512
            $length = 512
            # ASSOCF_IS_PROTOCOL, ASSOCSTR_PROGID
            [void][HyperHarborAssoc]::AssocQueryString(0x1000, 20, $protocol, $null, $buffer, [ref]$length)
            $buffer.ToString()
        }
        $choice = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice' -ErrorAction SilentlyContinue
        $policy = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -ErrorAction SilentlyContinue).DefaultAssociationsConfiguration
        @{
            http = ProgId 'http'
            https = ProgId 'https'
            userChoice = [string]$choice.ProgId
            userChoiceHash = [bool]$choice.Hash
            policy = [string]$policy
            policyFile = [bool]($policy -and (Test-Path -LiteralPath $policy))
        }
        """;

    /// <summary>
    /// The size of the session's desktop in pixels, whatever its scale, and the script's session (to tell the signed-in
    /// session from another one). A DEVMODE filled in from PowerShell answered 0x0 (2026-10-27): the field set on a
    /// boxed struct did not reach the call.
    /// </summary>
    private const string ResolutionScript = """
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class HyperHarborScreen {
            [DllImport("user32.dll")]
            public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int index);
        }
        '@
        # Per monitor v2, so a scaled desktop reports pixels.
        [void][HyperHarborScreen]::SetThreadDpiAwarenessContext([IntPtr](-4))
        @{
            width = [HyperHarborScreen]::GetSystemMetrics(0)
            height = [HyperHarborScreen]::GetSystemMetrics(1)
            session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        }
        """;

    private const string BraveKeyFunction = """
        Add-Type -AssemblyName System.Security
        function BraveKey {
            $state = Join-Path $env:LOCALAPPDATA 'BraveSoftware\Brave-Browser\User Data\Local State'
            if (-not (Test-Path -LiteralPath $state)) { return 'no Local State' }
            $key = [Convert]::FromBase64String((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).os_crypt.encrypted_key)
            try {
                [void][Security.Cryptography.ProtectedData]::Unprotect($key[5..($key.Length - 1)], $null, 'CurrentUser')
                'ok'
            }
            catch { "does not open: $($_.Exception.Message)" }
        }

        """;

    /// <summary>
    /// Protects a random text for the user with DPAPI, and starts Brave once so it makes its own DPAPI-protected key, then
    /// checks that key opens.
    /// </summary>
    private const string ProtectScript = BraveKeyFunction + """
        $text = [guid]::NewGuid().ToString()
        $blob = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($text), $null, 'CurrentUser')
        $brave = Join-Path $env:ProgramFiles 'BraveSoftware\Brave-Browser\Application\brave.exe'
        if (Test-Path -LiteralPath $brave) {
            $process = Start-Process -FilePath $brave -ArgumentList '--no-first-run', 'about:blank' -PassThru
            Start-Sleep -Seconds 20
            Get-Process brave -ErrorAction SilentlyContinue | Stop-Process -Force
        }
        @{ text = $text; blob = [Convert]::ToBase64String($blob); brave = BraveKey }
        """;

    private const string UnprotectScript = "param($blob, $text)\r\n" + BraveKeyFunction + """
        try {
            $opened = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($blob), $null, 'CurrentUser'))
            $data = if ($opened -eq $text) { 'ok' } else { 'different text' }
        }
        catch { $data = "does not open: $($_.Exception.Message)" }
        @{ data = $data; brave = BraveKey }
        """;

    private static IEnumerable<IntPtr> Windows(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid && IsWindowVisible(window))
            {
                found.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Answers Yes to "The remote computer could not be authenticated due to problems with its security certificate",
    /// as a person does for a new VM (its certificate is self-signed). A CertHash value under Terminal Server
    /// Client\Servers\&lt;address&gt; with the presented certificate's thumbprint did not stop the dialog (2026-10-27).
    /// Only that dialog is answered; any other one is left for the failure message.
    /// </summary>
    private static void AcceptCertificateWarning(int pid, Action<string> log)
    {
        foreach (var window in Windows(pid))
        {
            var children = new List<(IntPtr Handle, string Text)>();
            EnumChildWindows(window, (child, _) =>
            {
                children.Add((child, WindowTitle(child)));
                return true;
            }, IntPtr.Zero);
            if (children.Any(child => child.Text.Contains("could not be authenticated due to problems with its security certificate", StringComparison.Ordinal))
                && children.FirstOrDefault(child => child.Text == "&Yes") is { Handle: not 0 } yes)
            {
                log("Answered Yes to the certificate warning for the new VM.");
                PostMessage(yes.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private static List<string> ChildTexts(IntPtr window)
    {
        var texts = new List<string>();
        EnumChildWindows(window, (child, _) =>
        {
            if (WindowTitle(child) is { Length: > 0 } text)
            {
                texts.Add(text.ReplaceLineEndings(" "));
            }

            return true;
        }, IntPtr.Zero);
        return texts;
    }

    private static string WindowTitle(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
