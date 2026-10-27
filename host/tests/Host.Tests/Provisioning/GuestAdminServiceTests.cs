using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed class GuestAdminServiceTests : IDisposable
{
    private static readonly Guid VmId = Guid.NewGuid();
    private static readonly GuestAdminRequester Laptop = new(Guid.NewGuid(), "Laptop");
    private static readonly GuestCredential Admin = new("hhadmin", "Adm1n!");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeGuestAccountManager _guest = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly MemoryAuditLog _audit = new();
    private readonly UserStore _users;
    private readonly ProvisioningStore _provisioning;
    private readonly VmCredentialStore _credentials;
    private readonly GuestAdminStore _store;
    private readonly Guid _userId;
    private GuestAdminService? _service;

    public GuestAdminServiceTests()
    {
        _users = new UserStore(_directory, _time);
        _userId = _users.GetOrCreateDefault().UserId;
        _provisioning = new ProvisioningStore(_directory);
        _credentials = new VmCredentialStore(_directory);
        _store = new GuestAdminStore(_directory);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running));
        _provisioning.Save(new ProvisionedAccount(VmId, _userId, "hh-owner", _time.GetUtcNow(), _time.GetUtcNow()));
        _credentials.Save(VmId, Admin);
    }

    private GuestAdminService Service => _service ??= CreateService();

    public void Dispose()
    {
        _service?.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AccountName_FollowsTheUserLikeTheRemoteDesktopAccount()
    {
        Assert.Equal("hha-owner", GuestAdminAccountName.For("owner"));
        Assert.Equal("HyperHarbor administrator for owner", GuestAdminAccountName.FullName("owner"));
        Assert.Equal(VmAccountName.MaxLength, GuestAdminAccountName.For("a-very-long-user-name-indeed").Length);
    }

    [Fact]
    public async Task EnsureListed_CreatesTheAccountWithAPasswordNobodyGets_ThenLeavesItAlone()
    {
        await Service.EnsureListedAsync(VmId, _userId, CancellationToken.None);
        await Service.EnsureListedAsync(VmId, _userId, CancellationToken.None);

        Assert.Equal((VmId, "hha-owner", "HyperHarbor administrator for owner"), Assert.Single(_guest.AdminCreated));
        var (_, _, password) = Assert.Single(_guest.AdminEnabled);
        Assert.Equal(24, password.Length);
        Assert.Null(_store.Find(VmId, _userId));
        Assert.True(_guest.AdminAccounts[(VmId, "hha-owner")].IsReadyForElevation);
    }

    [Fact]
    public async Task Connecting_MakesTheAccountAvailableInTheBackground()
    {
        _inventory.Vms[0] = FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running, address: "192.168.0.50");
        using var rotator = new PasswordRotator(_guest, _credentials, new VmAccountPasswordStore(_directory), _time, TimeSpan.FromSeconds(60), Microsoft.Extensions.Logging.Abstractions.NullLogger<PasswordRotator>.Instance);
        var connect = new ConnectService(_inventory, _provisioning, rotator, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConnectService>.Instance, guestAdmin: Service);

        await connect.ConnectAsync(VmId, _userId, "Laptop", CancellationToken.None);
        await Service.WaitForBackgroundAsync();

        Assert.Equal((VmId, "hha-owner", "HyperHarbor administrator for owner"), Assert.Single(_guest.AdminCreated));
    }

    [Fact]
    public async Task EnsureListed_LeavesAnOpenWindowsPassphrase()
    {
        var window = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);

        await Service.EnsureListedAsync(VmId, _userId, CancellationToken.None);

        Assert.Equal([window.Passphrase], _guest.AdminEnabled.Select(enabled => enabled.Password));
    }

    [Fact]
    public async Task EnsureListed_SkipsLinuxAndVmsNotSetUp()
    {
        var other = Guid.NewGuid();
        _provisioning.Save(new ProvisionedAccount(other, _userId, "hh-owner", _time.GetUtcNow(), _time.GetUtcNow(), GuestOs: GuestOsFamily.Linux));
        _credentials.Save(other, Admin);

        await Service.EnsureListedAsync(other, _userId, CancellationToken.None);
        await Service.EnsureListedAsync(Guid.NewGuid(), _userId, CancellationToken.None);

        Assert.Empty(_guest.AdminCredentialsUsed);
    }

    [Fact]
    public async Task ClosingAWindow_LeavesTheAccountEnabled_WithANewPassword()
    {
        var window = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);

        Assert.True(await Service.CloseAsync(VmId, _userId, CancellationToken.None));

        var (_, _, password) = Assert.Single(_guest.AdminLocked);
        Assert.NotEqual(window.Passphrase, password);
        Assert.True(_guest.AdminAccounts[(VmId, "hha-owner")].Enabled);
    }

    [Fact]
    public async Task FirstWindow_CreatesTheAccount_LaterWindowsReuseIt()
    {
        var first = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        Assert.True(await Service.CloseAsync(VmId, _userId, CancellationToken.None));
        var second = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);

        Assert.Equal("hha-owner", first.AccountName);
        Assert.Equal((VmId, "hha-owner", "HyperHarbor administrator for owner"), Assert.Single(_guest.AdminCreated));
        Assert.Equal([first.Passphrase, second.Passphrase], _guest.AdminEnabled.Select(enabled => enabled.Password));
        Assert.NotEqual(first.Passphrase, second.Passphrase);
        Assert.All(_guest.AdminCredentialsUsed, credential => Assert.Equal(Admin, credential));
    }

    [Fact]
    public async Task Open_EnablesWithThePassphrase_Close_LocksWithARandomPassword()
    {
        var window = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        Assert.True(_guest.AdminAccounts[(VmId, "hha-owner")].Enabled);
        Assert.Equal(_time.GetUtcNow().AddMinutes(5), window.ExpiresAt);
        Assert.Equal(window.Passphrase, Assert.Single(_guest.AdminEnabled).Password);

        Assert.True(await Service.CloseAsync(VmId, _userId, CancellationToken.None));

        Assert.True(_guest.AdminAccounts[(VmId, "hha-owner")].Enabled);
        var disabled = Assert.Single(_guest.AdminLocked);
        Assert.Equal("hha-owner", disabled.Account);
        Assert.NotEqual(window.Passphrase, disabled.Password);
        Assert.Null(_store.Find(VmId, _userId));
        Assert.False((await Service.GetStatusAsync(VmId, _userId, CancellationToken.None)).Open);
        Assert.False(await Service.CloseAsync(VmId, _userId, CancellationToken.None));
    }

    [Fact]
    public async Task OpenAgainWithinTheWindow_ReturnsTheSamePassphrase_WithoutASecondRotation()
    {
        var first = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(4));

        var second = await Service.OpenAsync(VmId, _userId, new GuestAdminRequester(Guid.NewGuid(), "Phone"), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Single(_guest.AdminEnabled);
    }

    [Fact]
    public async Task ConcurrentOpens_RotateOnce()
    {
        _guest.Delay = TimeSpan.FromMilliseconds(50);

        var windows = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None)));

        Assert.Single(_guest.AdminEnabled);
        Assert.Single(windows.Select(window => window.Passphrase).Distinct());
    }

    [Fact]
    public async Task Expiry_ZeroesThePassphrase_AndLocksTheAccount()
    {
        var window = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        var buffer = Service.PassphraseBuffer(VmId, _userId)!;
        Assert.Equal(window.Passphrase, new string(buffer));

        _time.Advance(TimeSpan.FromMinutes(5));
        await Service.WaitForBackgroundAsync();

        Assert.All(buffer, c => Assert.Equal('\0', c));
        Assert.Null(Service.PassphraseBuffer(VmId, _userId));
        Assert.NotEmpty(_guest.AdminLocked);
        Assert.Null(_store.Find(VmId, _userId));
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((GuestAdminAudit.CloseAction, AuditOutcome.Succeeded, VmId, _userId, Laptop.DeviceId), (entry.Action, entry.Outcome, entry.VmId, entry.UserId, entry.DeviceId));
        Assert.Equal($"account=hha-owner, expiresAt={window.ExpiresAt:u}, reason=expired", entry.Detail);
    }

    [Fact]
    public async Task UserClose_ZeroesThePassphrase()
    {
        await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        var buffer = Service.PassphraseBuffer(VmId, _userId)!;

        await Service.CloseAsync(VmId, _userId, CancellationToken.None);

        Assert.All(buffer, c => Assert.Equal('\0', c));

        // The request that ended it is audited by the API; the service adds nothing.
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task VmStopping_EndsTheWindow_AndTheAccountIsLockedWhenTheVmRunsAgain()
    {
        await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        _inventory.SetState(VmId, VmState.Off);

        await Service.SweepAsync(CancellationToken.None);

        var status = await Service.GetStatusAsync(VmId, _userId, CancellationToken.None);
        Assert.Equal(new GuestAdminStatus("hha-owner", false, null, true), status);
        Assert.Null(Service.PassphraseBuffer(VmId, _userId));
        Assert.Empty(_guest.AdminLocked);

        _inventory.SetState(VmId, VmState.Running);
        await Service.SweepAsync(CancellationToken.None);

        Assert.Single(_guest.AdminLocked);
        Assert.False((await Service.GetStatusAsync(VmId, _userId, CancellationToken.None)).ClosePending);
        Assert.EndsWith("reason=vmStopped", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task Startup_ClosesWindowsAnEarlierRunLeftOpen()
    {
        _store.Save(new GuestAdminRecord(VmId, _userId, "hha-owner", _time.GetUtcNow().AddMinutes(5), Laptop.DeviceId, Laptop.DeviceName));
        _guest.AdminAccounts[(VmId, "hha-owner")] = new GuestAdminAccountState(true, true, true, false, true, true);

        // A new service is a new host process; the record is all it knows.
        var status = await Service.GetStatusAsync(VmId, _userId, CancellationToken.None);
        Assert.False(status.Open);
        Assert.True(status.ClosePending);

        await Service.SweepAsync(CancellationToken.None);

        Assert.NotEmpty(_guest.AdminLocked);
        Assert.Null(_store.Find(VmId, _userId));
        Assert.EndsWith("reason=hostStartup", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task PendingClose_WaitsWhileTheGuestIsUnreachable()
    {
        await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        _guest.AdminFailure = new GuestUnavailableException("PowerShell Direct could not connect.");

        await Assert.ThrowsAsync<GuestUnavailableException>(() => Service.CloseAsync(VmId, _userId, CancellationToken.None));
        Assert.True(_store.Find(VmId, _userId)!.ClosePending);
        await Service.SweepAsync(CancellationToken.None);
        Assert.NotNull(_store.Find(VmId, _userId));

        _guest.AdminFailure = null;
        await Service.SweepAsync(CancellationToken.None);

        Assert.Null(_store.Find(VmId, _userId));
        Assert.EndsWith("reason=user", Assert.Single(_audit.Entries).Detail);
    }

    [Fact]
    public async Task DeletedVm_IsForgotten()
    {
        await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        _inventory.Vms.Clear();

        await Service.WaitForBackgroundAsync();

        Assert.Null(_store.Find(VmId, _userId));
        Assert.Empty(_guest.AdminLocked);
    }

    [Fact]
    public async Task LinuxGuest_IsUnsupported()
    {
        _inventory.Vms[0] = FakeVmInventory.CreateVm(VmId, "Ubuntu", VmState.Running, GuestOsFamily.Linux);

        var error = await Assert.ThrowsAsync<GuestAdminConflictException>(() => Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.GuestAdminUnsupported, error.Code);
        Assert.Empty(_guest.TargetsUsed);
    }

    [Fact]
    public async Task StoppedVm_IsRefused()
    {
        _inventory.SetState(VmId, VmState.Off);

        var error = await Assert.ThrowsAsync<GuestAdminConflictException>(() => Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.VmNotRunning, error.Code);
    }

    [Fact]
    public async Task UnprovisionedVm_IsRefused()
    {
        var other = Guid.NewGuid();
        _inventory.Vms.Add(FakeVmInventory.CreateVm(other, "New VM", VmState.Running));
        _provisioning.Save(new ProvisionedAccount(Guid.NewGuid(), _userId, "hh-owner", _time.GetUtcNow(), _time.GetUtcNow()));

        var error = await Assert.ThrowsAsync<GuestAdminConflictException>(() => Service.OpenAsync(other, _userId, Laptop, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.VmNotProvisioned, error.Code);
        await Assert.ThrowsAsync<VmNotFoundException>(() => Service.OpenAsync(Guid.NewGuid(), _userId, Laptop, CancellationToken.None));
    }

    [Fact]
    public async Task UnreachableGuest_IsAConflict_AndTheAccountIsClosedLater()
    {
        _guest.AdminFailure = new GuestUnavailableException("PowerShell Direct could not connect to the guest.");

        var error = await Assert.ThrowsAsync<GuestAdminConflictException>(() => Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None));
        await Service.WaitForBackgroundAsync();

        Assert.Equal(ContractInfo.ProblemCodes.GuestUnavailable, error.Code);
        Assert.Equal(GuestAdminCloseReason.OpenFailed, _store.Find(VmId, _userId)!.CloseReason);
    }

    [Fact]
    public async Task UnverifiedAccount_FailsTheOpen_AndIsLocked()
    {
        _guest.AdminInRemoteDesktopUsers = true;

        var error = await Assert.ThrowsAsync<GuestOperationException>(() => Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None));
        await Service.WaitForBackgroundAsync();

        Assert.Contains("in Remote Desktop Users: True", error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(_guest.AdminLocked);
        Assert.Null(_store.Find(VmId, _userId));
        Assert.EndsWith("reason=openFailed", Assert.Single(_audit.Entries).Detail);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    public void Settings_OutsideTheRange_AreRefused(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Service.SetSettings(new GuestAdminSettings(minutes)));
        Assert.Equal(GuestAdminSettings.DefaultMinutes, Service.Settings.WindowMinutes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(30)]
    public async Task Settings_SetTheWindowLength(int minutes)
    {
        Assert.Equal(minutes, Service.SetSettings(new GuestAdminSettings(minutes)).WindowMinutes);

        var window = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);

        Assert.Equal(_time.GetUtcNow().AddMinutes(minutes), window.ExpiresAt);
        Assert.Equal(minutes, new GuestAdminStore(_directory).WindowMinutes);
    }

    [Fact]
    public async Task Passphrase_IsNeverLoggedPersistedOrInAnError()
    {
        var window = await Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None);
        Assert.DoesNotContain(window.Passphrase, window.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(window.Passphrase, File.ReadAllText(Path.Combine(_directory, GuestAdminStore.FileName)), StringComparison.Ordinal);
        _time.Advance(TimeSpan.FromMinutes(5));
        await Service.WaitForBackgroundAsync();

        // A guest that echoes what it was given into its error output.
        _guest.EchoSecretsInFailure = true;
        var error = await Assert.ThrowsAsync<GuestOperationException>(() => Service.OpenAsync(VmId, _userId, Laptop, CancellationToken.None));
        var echoed = _guest.TargetsUsed.Count;
        await Service.WaitForBackgroundAsync();

        var secrets = _guest.AdminEnabled.Select(enabled => enabled.Password).Append(window.Passphrase).Append(Admin.Password).ToArray();
        Assert.True(echoed > 0);
        Assert.DoesNotContain("Adm1n!", error.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", error.Message, StringComparison.Ordinal);
        _logs.AssertNoneContain(secrets);
        Assert.All(_audit.Entries, entry => Assert.DoesNotContain(window.Passphrase, entry.Detail ?? string.Empty, StringComparison.Ordinal));
    }

    private GuestAdminService CreateService() => new(
        _inventory,
        _users,
        _provisioning,
        _credentials,
        _guest,
        _store,
        _audit,
        _time,
        new LoggerFactory([_logs]).CreateLogger<GuestAdminService>());

    private sealed class MemoryAuditLog : IAuditLog
    {
        private readonly List<AuditEntry> _entries = [];

        public IReadOnlyList<AuditEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public void Write(AuditEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }
}
