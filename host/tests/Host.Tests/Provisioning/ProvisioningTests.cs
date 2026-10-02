using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed class ProvisioningTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private const string AdminPassword = "Adm1n-Secret!";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeGuestAccountManager _guest = new();
    private readonly UserStore _users;
    private readonly VmCredentialStore _credentials;
    private readonly ProvisioningStore _provisioning;
    private readonly ProvisioningService _service;
    private readonly Guid _userId;

    public ProvisioningTests()
    {
        _users = new UserStore(_directory);
        _userId = _users.GetOrCreateDefault().UserId;
        _credentials = new VmCredentialStore(_directory);
        _provisioning = new ProvisioningStore(_directory);
        _service = new ProvisioningService(_inventory, _users, _guest, _credentials, _provisioning, TimeProvider.System, NullLogger<ProvisioningService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Windows 11 Dev", VmState.Running));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Provision_CreatesVerifiedAccount_AndStoresAdminCredential()
    {
        var result = await _service.ProvisionAsync(VmId, _userId, Request(), CancellationToken.None);

        Assert.Equal("hh-owner", result.AccountName);
        Assert.Equal("hh-owner", Assert.Single(_guest.PasswordsSet).Account);
        Assert.NotNull(_provisioning.Find(VmId, _userId));
        Assert.Equal(new GuestCredential("Administrator", AdminPassword), _credentials.Find(VmId));
    }

    [Fact]
    public async Task Provision_DoesNotPersistAnyPasswordInPlaintext()
    {
        await _service.ProvisionAsync(VmId, _userId, Request(), CancellationToken.None);
        var initialPassword = Assert.Single(_guest.PasswordsSet).Password;

        foreach (var file in Directory.GetFiles(_directory))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(AdminPassword, text);
            Assert.DoesNotContain(initialPassword, text);
        }
    }

    [Fact]
    public async Task Provision_VerificationFailure_LeavesVmUnprovisioned()
    {
        _guest.SkipGroupMembership = true;

        var error = await Assert.ThrowsAsync<GuestOperationException>(() => _service.ProvisionAsync(VmId, _userId, Request(), CancellationToken.None));

        Assert.Contains("could not be verified", error.Message);
        Assert.Null(_provisioning.Find(VmId, _userId));
        Assert.Null(_credentials.Find(VmId));
    }

    [Theory]
    [InlineData(typeof(GuestCredentialRejectedException))]
    [InlineData(typeof(GuestAccountConflictException))]
    [InlineData(typeof(GuestUnavailableException))]
    public async Task Provision_GuestErrors_StoreNothing(Type errorType)
    {
        _guest.Failure = (Exception)Activator.CreateInstance(errorType, "guest said no")!;

        await Assert.ThrowsAsync(errorType, () => _service.ProvisionAsync(VmId, _userId, Request(), CancellationToken.None));

        Assert.Null(_provisioning.Find(VmId, _userId));
        Assert.Null(_credentials.Find(VmId));
    }

    [Fact]
    public async Task Provision_StoppedVm_IsConflict()
    {
        _inventory.SetState(VmId, VmState.Off);

        await Assert.ThrowsAsync<GuestAccountConflictException>(() => _service.ProvisionAsync(VmId, _userId, Request(), CancellationToken.None));
        Assert.Empty(_guest.AdminCredentialsUsed);
    }

    [Fact]
    public async Task Provision_UnknownVm_IsNotFound()
    {
        await Assert.ThrowsAsync<VmNotFoundException>(() => _service.ProvisionAsync(Guid.NewGuid(), _userId, Request(), CancellationToken.None));
    }

    [Fact]
    public void CredentialStore_RoundTripsAndEncryptsFile()
    {
        _credentials.Save(VmId, new GuestCredential("Administrator", AdminPassword));

        Assert.Equal(new GuestCredential("Administrator", AdminPassword), new VmCredentialStore(_directory).Find(VmId));
        Assert.DoesNotContain("Administrator", File.ReadAllText(Path.Combine(_directory, "vm-credentials.json.protected")));
    }

    [Fact]
    public void GuestCredential_HidesPasswordInToString()
    {
        Assert.DoesNotContain(AdminPassword, new GuestCredential("Administrator", AdminPassword).ToString());
    }

    [Fact]
    public void PasswordGenerator_MeetsComplexityAndIsUnique()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => PasswordGenerator.Generate()).ToList();

        Assert.All(passwords, password =>
        {
            Assert.Equal(24, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));
        });
        Assert.Equal(passwords.Count, passwords.Distinct().Count());
    }

    [Theory]
    [InlineData("""{"ok":true,"result":{"exists":true}}""", null)]
    [InlineData("""{"ok":false,"stage":"connect","error":"The credential is invalid."}""", typeof(GuestCredentialRejectedException))]
    [InlineData("""{"ok":false,"stage":"connect","error":"The virtual machine is not running."}""", typeof(GuestUnavailableException))]
    [InlineData("""{"ok":false,"stage":"notLocal","error":"An account named hh-owner exists but is not a local account."}""", typeof(GuestAccountConflictException))]
    [InlineData("""{"ok":false,"stage":"guest","error":"Access to the registry key is denied."}""", typeof(GuestOperationException))]
    [InlineData("not json at all", typeof(GuestOperationException))]
    public void PowerShellDirect_InterpretsScriptResults(string output, Type? expectedError)
    {
        if (expectedError is null)
        {
            Assert.Equal(true, (bool?)PowerShellDirectAccountManager.Interpret(output, string.Empty)!["exists"]);
            return;
        }

        Assert.Throws(expectedError, () => PowerShellDirectAccountManager.Interpret("WARNING: noise\n" + output, string.Empty));
    }

    private static ProvisionVmRequest Request() => new("Administrator", AdminPassword);
}
