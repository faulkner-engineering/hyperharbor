using System.Text;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed class LinuxProvisioningTests : IDisposable
{
    private static readonly Guid LinuxVm = Guid.Parse("62671de3-fe20-48d6-a7c4-fced2c5fd6f4");
    private const string Address = "172.25.190.7";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeGuestAccountManager _guest = new();
    private readonly ProvisioningStore _provisioning;
    private readonly PasswordRotator _rotator;
    private readonly ProvisioningService _service;
    private readonly ConnectService _connect;
    private readonly Guid _userId;

    public LinuxProvisioningTests()
    {
        var users = new UserStore(_directory);
        _userId = users.GetOrCreateDefault().UserId;
        var credentials = new VmCredentialStore(_directory);
        _provisioning = new ProvisioningStore(_directory);
        _rotator = new PasswordRotator(_guest, credentials, TimeProvider.System, TimeSpan.FromSeconds(60), NullLogger<PasswordRotator>.Instance);
        _service = new ProvisioningService(_inventory, users, _guest, credentials, _provisioning, TimeProvider.System, NullLogger<ProvisioningService>.Instance);
        _connect = new ConnectService(_inventory, _provisioning, _rotator, NullLogger<ConnectService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(LinuxVm, "Ubuntu Dev", VmState.Running, GuestOsFamily.Linux, Address));
    }

    public void Dispose()
    {
        _rotator.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ProvisionVmRequest Request(bool installDesktop = false, bool trustNewHostKey = false) =>
        new("hhadmin", "Adm1n-Secret!", true, installDesktop, trustNewHostKey);

    [Fact]
    public async Task ProvisionAgain_KeepsThePinnedHostKey()
    {
        await _service.ProvisionAsync(LinuxVm, _userId, Request(), CancellationToken.None);
        _guest.TargetsUsed.Clear();

        await _service.ProvisionAsync(LinuxVm, _userId, Request(), CancellationToken.None);

        Assert.Equal(FakeGuestAccountManager.FakeHostKey, _guest.TargetsUsed[0].SshHostKey);
    }

    [Fact]
    public async Task ProvisionAgain_ChangedHostKey_FailsBeforeTheCredentialIsUsed()
    {
        await _service.ProvisionAsync(LinuxVm, _userId, Request(), CancellationToken.None);
        _guest.AdminCredentialsUsed.Clear();
        _guest.PresentedHostKey = "SHA256:impostor";

        await Assert.ThrowsAsync<GuestAccountConflictException>(
            () => _service.ProvisionAsync(LinuxVm, _userId, Request(), CancellationToken.None));

        Assert.Empty(_guest.AdminCredentialsUsed);
        Assert.Equal(FakeGuestAccountManager.FakeHostKey, _provisioning.Find(LinuxVm, _userId)!.SshHostKey);
    }

    [Fact]
    public async Task ProvisionAgain_TrustNewHostKey_PinsTheNewKey()
    {
        await _service.ProvisionAsync(LinuxVm, _userId, Request(), CancellationToken.None);
        _guest.PresentedHostKey = "SHA256:reinstalled";

        await _service.ProvisionAsync(LinuxVm, _userId, Request(trustNewHostKey: true), CancellationToken.None);

        Assert.Equal("SHA256:reinstalled", _provisioning.Find(LinuxVm, _userId)!.SshHostKey);
    }

    [Fact]
    public async Task Provision_Linux_UsesSshTarget_AndPinsHostKey()
    {
        await _service.ProvisionAsync(LinuxVm, _userId, Request(installDesktop: true), CancellationToken.None);

        var provisionTarget = _guest.TargetsUsed[0];
        Assert.Equal(new GuestTarget(LinuxVm, GuestOsFamily.Linux, Address, null), provisionTarget);
        Assert.Equal(FakeGuestAccountManager.FakeHostKey, _guest.TargetsUsed[1].SshHostKey);
        Assert.Equal(new GuestProvisionOptions(true, true), Assert.Single(_guest.ProvisionOptions));

        var account = _provisioning.Find(LinuxVm, _userId)!;
        Assert.Equal(GuestOsFamily.Linux, account.GuestOs);
        Assert.Equal(FakeGuestAccountManager.FakeHostKey, account.SshHostKey);
    }

    [Fact]
    public async Task Connect_Linux_ReturnsPlainUserName_AndRotatesWithPinnedKey()
    {
        await _service.ProvisionAsync(LinuxVm, _userId, Request(), CancellationToken.None);

        var connection = await _connect.ConnectAsync(LinuxVm, _userId, "laptop", CancellationToken.None);

        Assert.Equal("hh-owner", connection.UserName);
        Assert.Equal(GuestOsFamily.Linux, connection.GuestOs);
        Assert.Equal(Address, connection.Address);
        Assert.Equal(new GuestTarget(LinuxVm, GuestOsFamily.Linux, Address, FakeGuestAccountManager.FakeHostKey), _guest.TargetsUsed[^1]);
    }

    [Fact]
    public async Task Provision_UnknownGuestOs_IsConflict()
    {
        var vm = Guid.NewGuid();
        _inventory.Vms.Add(FakeVmInventory.CreateVm(vm, "Booting", VmState.Running, GuestOsFamily.Unknown, Address));

        var error = await Assert.ThrowsAsync<GuestAccountConflictException>(() => _service.ProvisionAsync(vm, _userId, Request(), CancellationToken.None));

        Assert.Contains("cannot tell which operating system", error.Message);
        Assert.Empty(_guest.TargetsUsed);
    }

    [Fact]
    public async Task Provision_LinuxWithoutAddress_IsConflict()
    {
        var vm = Guid.NewGuid();
        _inventory.Vms.Add(FakeVmInventory.CreateVm(vm, "No network", VmState.Running, GuestOsFamily.Linux));

        await Assert.ThrowsAsync<GuestAccountConflictException>(() => _service.ProvisionAsync(vm, _userId, Request(), CancellationToken.None));
        Assert.Empty(_guest.TargetsUsed);
    }

    [Fact]
    public void ProvisioningStore_LegacyRecordWithoutGuestOs_IsWindows()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "vm-provisioning.json"),
            $$"""[{"VmId":"{{LinuxVm}}","UserId":"{{_userId}}","AccountName":"hh-owner","ProvisionedAt":"2026-10-02T12:00:00+00:00","VerifiedAt":"2026-10-02T12:00:00+00:00"}]""");

        var account = new ProvisioningStore(_directory).Find(LinuxVm, _userId)!;

        Assert.Equal(GuestOsFamily.Windows, account.GuestOs);
        Assert.Null(account.SshHostKey);
    }

    [Fact]
    public async Task Router_UnknownOs_IsConflict()
    {
        var router = new GuestAccountRouter(_guest, _guest);

        await Assert.ThrowsAsync<GuestAccountConflictException>(() =>
            router.SetPasswordAsync(new GuestTarget(LinuxVm, GuestOsFamily.Unknown), new GuestCredential("a", "b"), "hh-owner", "pw", CancellationToken.None));
    }

    [Fact]
    public void Ssh_Interpret_ParsesInspectResult()
    {
        var values = SshAccountManager.Interpret("apt noise\nHH-RESULT ok exists=1 local=1 enabled=1 rdp=1 desktop=0\n", "");

        var state = SshAccountManager.ParseState(values);
        Assert.True(state.Exists && state.IsLocal && state.Enabled);
        Assert.False(state.RemoteDesktopAllowed);
        Assert.True(SshAccountManager.ParseState(SshAccountManager.Interpret("HH-RESULT ok exists=1 local=1 enabled=1 rdp=1 desktop=1", "")).IsReadyForRemoteDesktop);
    }

    [Theory]
    [InlineData("HH-RESULT error notLocal An account named hh-owner exists but is not a local account.", typeof(GuestAccountConflictException))]
    [InlineData("HH-RESULT error noDesktop No desktop environment is installed in the VM.", typeof(GuestAccountConflictException))]
    [InlineData("HH-RESULT error guest Starting the xrdp service failed.", typeof(GuestOperationException))]
    [InlineData("bash: command not found", typeof(GuestOperationException))]
    public void Ssh_Interpret_MapsErrors(string stdout, Type expected)
    {
        var error = Assert.Throws(expected, () => SshAccountManager.Interpret(stdout, "last stderr line"));

        if (stdout.StartsWith("HH-RESULT", StringComparison.Ordinal))
        {
            Assert.Contains(stdout.Split(' ', 4)[3], error.Message);
        }
    }

    [Fact]
    public void Ssh_CommandLine_CarriesNoSecretsAndDecodesToLfScript()
    {
        var command = SshAccountManager.CommandLine(asRoot: false, ["provision", "hh-owner", "1", "0"]);

        Assert.StartsWith("sudo -S -k -p '' bash -c \"$(printf %s ", command);
        Assert.EndsWith("| base64 -d)\" hyperharbor 'provision' 'hh-owner' '1' '0'", command);

        var encoded = command.Split(' ')[9];
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.DoesNotContain('\r', script);
        Assert.Contains("chpasswd", script);
        Assert.DoesNotContain("sudo", SshAccountManager.CommandLine(asRoot: true, ["inspect", "hh-owner"]));
    }
}
