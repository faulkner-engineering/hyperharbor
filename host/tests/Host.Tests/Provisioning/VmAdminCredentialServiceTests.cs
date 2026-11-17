using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed class VmAdminCredentialServiceTests : IDisposable
{
    private const string StoredPassword = "Stored-Secret1!";
    private const string NewPassword = "Typed-Secret2!";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeGuestAccountManager _guest = new();
    private readonly VmCredentialStore _credentials;
    private readonly ProvisioningStore _provisioning;
    private readonly VmAdminCredentialStatusStore _status;
    private readonly VmAdminCredentialService _service;

    public VmAdminCredentialServiceTests()
    {
        _credentials = new VmCredentialStore(_directory);
        _provisioning = new ProvisioningStore(_directory);
        _status = new VmAdminCredentialStatusStore(_directory);
        _service = new VmAdminCredentialService(_inventory, _guest, _credentials, _provisioning, _status, _time, NullLogger<VmAdminCredentialService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Get_WithoutACredential_HasNoAccount_AndAnUnknownVmIsNotFound()
    {
        Assert.Equal(new VmAdminCredential(null, false, VmAdminCredentialState.Unchecked, null, null, null), await _service.GetAsync(VmId, CancellationToken.None));
        await Assert.ThrowsAsync<VmNotFoundException>(() => _service.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Get_NeverContactsTheGuest()
    {
        Store(managed: true);

        var result = await _service.GetAsync(VmId, CancellationToken.None);

        Assert.Equal(new VmAdminCredential("hhadmin", true, VmAdminCredentialState.Unchecked, null, null, null), result);
        Assert.Empty(_guest.TargetsUsed);
    }

    [Fact]
    public async Task Check_RecordsAWorkingCredentialAndItsExpiry_WithoutChangingTheAccount()
    {
        Store(managed: false);
        var expires = _time.GetUtcNow().AddDays(20);
        _guest.Administrator = new GuestAdministrator(true, expires);

        var result = await _service.CheckAsync(VmId, passwordNeverExpires: false, CancellationToken.None);

        Assert.Equal(new VmAdminCredential("hhadmin", false, VmAdminCredentialState.Working, _time.GetUtcNow(), expires, null), result);
        Assert.Equal(expires, _guest.Administrator.PasswordExpiresAt);
        Assert.False(Assert.Single(_guest.AdministratorChecks).KeepPassword);
    }

    [Fact]
    public async Task Check_OfAManagedAccount_TurnsOffExpiry()
    {
        Store(managed: true);
        _guest.Administrator = new GuestAdministrator(true, _time.GetUtcNow().AddDays(2));

        var result = await _service.CheckAsync(VmId, passwordNeverExpires: false, CancellationToken.None);

        Assert.Null(result.PasswordExpiresAt);
        Assert.Null(_service.AlertFor(VmId));
    }

    [Fact]
    public async Task Check_OnRequest_TurnsOffExpiryForTheUsersOwnAccount()
    {
        Store(managed: false);
        _guest.Administrator = new GuestAdministrator(true, _time.GetUtcNow().AddDays(2));

        var result = await _service.CheckAsync(VmId, passwordNeverExpires: true, CancellationToken.None);

        Assert.Null(result.PasswordExpiresAt);
        Assert.False(result.Managed);
    }

    [Fact]
    public async Task Check_WhenExpiryCannotBeTurnedOff_SaysSo_AndKeepsTheDate()
    {
        Store(managed: false);
        var expires = _time.GetUtcNow().AddDays(2);
        _guest.Administrator = new GuestAdministrator(true, expires);
        _guest.KeepPasswordFails = true;

        var error = await Assert.ThrowsAsync<GuestOperationException>(() => _service.CheckAsync(VmId, passwordNeverExpires: true, CancellationToken.None));

        Assert.Contains("Access is denied.", error.Message, StringComparison.Ordinal);
        Assert.Equal(expires, _status.Find(VmId)!.PasswordExpiresAt);
    }

    [Fact]
    public async Task Check_OfARejectedCredential_IsAResult_WithoutTheSecret()
    {
        Store(managed: true);
        _guest.RejectedAdminPasswords.Add(StoredPassword);

        var result = await _service.CheckAsync(VmId, passwordNeverExpires: false, CancellationToken.None);

        Assert.Equal(VmAdminCredentialState.Rejected, result.State);
        Assert.Contains("The credential is invalid.", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(StoredPassword, result.Detail, StringComparison.Ordinal);
        Assert.Equal(new VmAdminCredentialAlert(VmAdminCredentialAlertKind.Rejected, null), _service.AlertFor(VmId));
    }

    [Fact]
    public async Task Check_OfAnAccountThatIsNoAdministrator_IsRejected()
    {
        Store(managed: false);
        _guest.Administrator = new GuestAdministrator(false, null);

        var result = await _service.CheckAsync(VmId, passwordNeverExpires: false, CancellationToken.None);

        Assert.Equal(VmAdminCredentialState.Rejected, result.State);
        Assert.Equal("hhadmin signed in to Dev Box but is not an administrator there.", result.Detail);
    }

    [Fact]
    public async Task Check_NeedsARunningVmWithACredential()
    {
        var missing = await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.CheckAsync(VmId, false, CancellationToken.None));
        Assert.Equal(ContractInfo.ProblemCodes.VmNotProvisioned, missing.Code);

        Store(managed: true);
        _inventory.SetState(VmId, VmState.Off);
        var stopped = await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.CheckAsync(VmId, false, CancellationToken.None));
        Assert.Equal(ContractInfo.ProblemCodes.VmNotRunning, stopped.Code);
        Assert.Empty(_guest.TargetsUsed);
    }

    [Fact]
    public async Task Check_WhenTheGuestIsUnreachable_ChangesNothing()
    {
        Store(managed: true);
        _guest.Failure = new GuestUnavailableException("PowerShell Direct could not connect to the guest.");

        await Assert.ThrowsAsync<GuestUnavailableException>(() => _service.CheckAsync(VmId, false, CancellationToken.None));

        Assert.Null(_status.Find(VmId));
    }

    [Fact]
    public async Task Replace_StoresTheCredentialTheGuestAccepts_AndLeavesTheUsersAccountAlone()
    {
        Store(managed: true);
        _status.RecordRejected(VmId, "The credential is invalid.", _time.GetUtcNow());

        var result = await _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest(" hhadmin ", NewPassword), CancellationToken.None);

        // The same account keeps its origin: HyperHarbor made it, so it stays managed.
        Assert.Equal(new GuestCredential("hhadmin", NewPassword, Managed: true), _credentials.Find(VmId));
        Assert.Equal(new VmAdminCredential("hhadmin", true, VmAdminCredentialState.Working, _time.GetUtcNow(), null, null), result);
        Assert.Null(_service.AlertFor(VmId));
        Assert.Empty(_guest.PasswordsSet);
        Assert.Empty(_guest.PasswordsChanged);
    }

    [Fact]
    public async Task Replace_WithAnotherAccount_IsNotManaged_AndCanTurnOffItsExpiry()
    {
        Store(managed: true);
        var expires = _time.GetUtcNow().AddDays(40);
        _guest.Administrator = new GuestAdministrator(true, expires);

        var kept = await _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest("Tyler", NewPassword), CancellationToken.None);

        Assert.Equal(new GuestCredential("Tyler", NewPassword), _credentials.Find(VmId));
        Assert.False(kept.Managed);
        Assert.Equal(expires, kept.PasswordExpiresAt);

        var off = await _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest("Tyler", NewPassword, PasswordNeverExpires: true), CancellationToken.None);

        Assert.Null(off.PasswordExpiresAt);
    }

    [Fact]
    public async Task Replace_WithACredentialTheGuestRejects_StoresNothing_AndKeepsTheSecretOut()
    {
        Store(managed: true);
        _guest.RejectedAdminPasswords.Add(NewPassword);

        var error = await Assert.ThrowsAsync<GuestCredentialRejectedException>(() =>
            _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest("hhadmin", NewPassword), CancellationToken.None));

        Assert.DoesNotContain(NewPassword, error.Message, StringComparison.Ordinal);
        Assert.Equal(new GuestCredential("hhadmin", StoredPassword, Managed: true), _credentials.Find(VmId));
        Assert.Null(_status.Find(VmId));
    }

    [Fact]
    public async Task Replace_WithAnAccountThatIsNoAdministrator_StoresNothing()
    {
        _guest.Administrator = new GuestAdministrator(false, null);

        await Assert.ThrowsAsync<GuestCredentialRejectedException>(() =>
            _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest("hh-owner", NewPassword), CancellationToken.None));

        Assert.Null(_credentials.Find(VmId));
    }

    [Theory]
    [InlineData("", "password", "userName")]
    [InlineData("hhadmin", "", "password")]
    public async Task Replace_RefusesAnEmptyNameOrPassword_BeforeTheGuestIsContacted(string userName, string password, string field)
    {
        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest(userName, password), CancellationToken.None));

        Assert.Equal(field, Assert.Single(error.Errors).Field);
        Assert.Empty(_guest.TargetsUsed);
    }

    [Fact]
    public async Task Replace_OnLinux_UsesThePinnedHostKey_AndRefusesAVmThatWasNeverSetUp()
    {
        _inventory.Vms[0] = FakeVmInventory.CreateVm(VmId, "Ubuntu", VmState.Running, GuestOsFamily.Linux, "192.168.0.51");

        var unpinned = await Assert.ThrowsAsync<LifecycleConflictException>(() =>
            _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest("hhadmin", NewPassword), CancellationToken.None));
        Assert.Equal(ContractInfo.ProblemCodes.VmNotProvisioned, unpinned.Code);
        Assert.Empty(_guest.TargetsUsed);

        _provisioning.Save(new ProvisionedAccount(VmId, UserId, "hh-owner", _time.GetUtcNow(), _time.GetUtcNow(), GuestOsFamily.Linux, FakeGuestAccountManager.FakeHostKey));
        await _service.ReplaceAsync(VmId, new SetVmAdminCredentialRequest("hhadmin", NewPassword), CancellationToken.None);

        Assert.Equal(new GuestTarget(VmId, GuestOsFamily.Linux, "192.168.0.51", FakeGuestAccountManager.FakeHostKey), Assert.Single(_guest.TargetsUsed));
    }

    [Theory]
    [InlineData(15, null)]
    [InlineData(14, VmAdminCredentialAlertKind.Expiring)]
    [InlineData(1, VmAdminCredentialAlertKind.Expiring)]
    [InlineData(-3, VmAdminCredentialAlertKind.Expiring)]
    public void Alert_StartsFourteenDaysBeforeThePasswordExpires_AndStaysAfterIt(int daysLeft, VmAdminCredentialAlertKind? expected)
    {
        var now = _time.GetUtcNow();
        var expires = now.AddDays(daysLeft);

        var alert = VmAdminCredentialService.Alert(new AdminCredentialStatus(VmId, VmAdminCredentialState.Working, now, expires), now);

        Assert.Equal(expected, alert?.Kind);
        Assert.Equal(expected is null ? null : expires, alert?.PasswordExpiresAt);
    }

    [Fact]
    public void Alert_IsNoneForAnUncheckedOrNeverExpiringCredential_AndRejectedWinsOverExpiring()
    {
        var now = _time.GetUtcNow();

        Assert.Null(VmAdminCredentialService.Alert(null, now));
        Assert.Null(VmAdminCredentialService.Alert(new AdminCredentialStatus(VmId, VmAdminCredentialState.Working, now), now));
        Assert.Equal(
            new VmAdminCredentialAlert(VmAdminCredentialAlertKind.Rejected, now.AddDays(-1)),
            VmAdminCredentialService.Alert(new AdminCredentialStatus(VmId, VmAdminCredentialState.Rejected, now, now.AddDays(-1), "expired"), now));
    }

    [Fact]
    public void TheStatusStore_KeepsTheExpiryDateAcrossARejection_PersistsAndForgets()
    {
        var now = _time.GetUtcNow();
        var expires = now.AddDays(5);
        _status.RecordWorking(VmId, new GuestAdministrator(true, expires), now);

        // A rotation that did not ask the guest about the administrator keeps the date read before.
        Assert.Equal(expires, _status.RecordWorking(VmId, null, now.AddMinutes(1)).PasswordExpiresAt);
        Assert.Equal(expires, _status.RecordRejected(VmId, "The credential is invalid.", now.AddMinutes(2)).PasswordExpiresAt);

        var reloaded = new VmAdminCredentialStatusStore(_directory);
        Assert.Equal(new AdminCredentialStatus(VmId, VmAdminCredentialState.Rejected, now.AddMinutes(2), expires, "The credential is invalid."), reloaded.Find(VmId));
        Assert.True(reloaded.Remove(VmId));
        Assert.False(reloaded.Remove(VmId));
        Assert.Null(new VmAdminCredentialStatusStore(_directory).Find(VmId));
    }

    [Fact]
    public void TheMigration_MarksTheAccountsUnattendedInstallsMade_AndNoOthers()
    {
        var ownAccount = Guid.NewGuid();
        var manual = Guid.NewGuid();
        var installs = new UnattendedInstallStore(_directory);
        installs.Save(Install(VmId));
        installs.Save(Install(ownAccount));
        _credentials.Save(VmId, new GuestCredential("hhadmin", StoredPassword));
        _credentials.Save(ownAccount, new GuestCredential("Tyler", StoredPassword));
        _credentials.Save(manual, new GuestCredential("hhadmin", StoredPassword));

        Assert.Equal(1, ManagedAdministratorMigration.Run(_directory));
        Assert.Equal(0, ManagedAdministratorMigration.Run(_directory));

        var migrated = new VmCredentialStore(_directory);
        Assert.Equal(new GuestCredential("hhadmin", StoredPassword, Managed: true), migrated.Find(VmId));
        Assert.False(migrated.Find(ownAccount)!.Managed);
        Assert.False(migrated.Find(manual)!.Managed);
    }

    [Fact]
    public void TheMigration_RunsFromDataFormatFour_AndLeavesUnreadableDataAlone()
    {
        new UnattendedInstallStore(_directory).Save(Install(VmId));
        _credentials.Save(VmId, new GuestCredential("hhadmin", StoredPassword));
        File.WriteAllText(Path.Combine(_directory, DataFormat.FileName), """{ "format": 4 }""");

        Assert.Equal(4, DataFormat.EnsureCurrent(_directory));

        Assert.Equal(DataFormat.Current, DataFormat.Read(_directory));
        Assert.True(new VmCredentialStore(_directory).Find(VmId)!.Managed);

        var unreadable = Path.Combine(_directory, "unreadable");
        Directory.CreateDirectory(unreadable);
        new UnattendedInstallStore(unreadable).Save(Install(VmId));
        File.WriteAllText(Path.Combine(unreadable, "vm-credentials.json.protected"), "not protected data");
        Assert.Equal(0, ManagedAdministratorMigration.Run(unreadable));
    }

    private void Store(bool managed) => _credentials.Save(VmId, new GuestCredential("hhadmin", StoredPassword, managed));

    private UnattendedInstall Install(Guid vmId) =>
        new(vmId, UserId, "windows-workstation", InstallOs.Windows, false, @"C:\seed.iso", UnattendedInstallState.Ready, "Ready", _time.GetUtcNow(), _time.GetUtcNow());
}
