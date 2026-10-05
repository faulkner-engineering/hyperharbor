using System.Text;
using DiscUtils.Iso9660;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Tests.Unattend;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

/// <summary>Creating a VM with an unattended install: the answer file, the second DVD, the credential, and the start.</summary>
public sealed class UnattendedCreationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVBuilder _builder = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeDiskFiles _files = new();
    private readonly FakePowerInvoker _power = new();
    private readonly FakeVmKeyboard _keyboard = new();
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly VmCredentialStore _credentials;
    private readonly UnattendedInstallStore _installs;
    private readonly Guid _userId;
    private readonly SetupProfileStore _setupProfiles;
    private readonly VmCreationService _creation;

    public UnattendedCreationTests()
    {
        var data = Path.Combine(_root, "data");
        var isos = Path.Combine(_root, "isos");
        TestIsos.Windows(Path.Combine(isos, "win11.iso"), "Windows 11 Home", "Windows 11 Pro");
        TestIsos.Ubuntu(Path.Combine(isos, "ubuntu.iso"));
        File.WriteAllBytes(Path.Combine(isos, "other.iso"), new byte[4096]);

        var users = new UserStore(data);
        _userId = users.GetOrCreateDefault().UserId;
        _credentials = new VmCredentialStore(data);
        _installs = new UnattendedInstallStore(data);
        _setupProfiles = new SetupProfileStore(data, Catalogs.Default);
        var unattended = new UnattendedSetup(
            new UnattendProfileStore(data, () => "Central Standard Time"), new IsoInspector(), users, _credentials, _installs,
            _power, _keyboard, TimeProvider.System, NullLogger<UnattendedSetup>.Instance, keyInterval: TimeSpan.Zero, _setupProfiles);

        _builder.OnDiskCreated = path => _files.Files.Add(path);
        var options = new LifecycleOptions { VmRootFolder = Path.Combine(_root, "vms") };
        _creation = new VmCreationService(
            _inventory, _builder, _storage, new FakeHyperVHost(), new FakeHostCapacity(), _files, new IsoLibrary(isos), _jobs, options,
            NullLogger<VmCreationService>.Instance, unattended: unattended);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static CreateVmRequest Request(string iso = "win11.iso", UnattendedInstallRequest? install = null, bool tpm = true) =>
        new("Dev Box", iso, 64, 2, 4096, 4096, false, null, tpm, false, install);

    private async Task<VmJobSnapshot> RunAsync(CreateVmRequest request)
    {
        var job = await _creation.StartAsync(_userId, request, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        return _jobs.Get(job.Id, _userId)!;
    }

    [Fact]
    public async Task Windows_AttachesTheAnswerFileStartsTheVmAndPressesAKey()
    {
        var job = await RunAsync(Request(install: new UnattendedInstallRequest("windows-workstation")));

        Assert.Equal(VmJobState.Succeeded, job.State);
        var vmId = _builder.CreatedVmId;
        var blueprint = _builder.Configured!;
        Assert.Equal(CimHyperVBuilder.MicrosoftWindowsTemplateId, blueprint.SecureBootTemplateId);
        Assert.Equal(SeedIso.PathFor(blueprint.DiskPath), blueprint.SeedIsoPath);

        using (var stream = File.OpenRead(blueprint.SeedIsoPath!))
        using (var reader = new CDReader(stream, joliet: true))
        {
            var xml = Encoding.UTF8.GetString(reader.ReadAllBytes("Autounattend.xml"));
            Assert.Contains("<Value>Windows 11 Pro</Value>", xml, StringComparison.Ordinal);
            Assert.Contains("<ComputerName>DEV-BOX</ComputerName>", xml, StringComparison.Ordinal);
            Assert.Contains("New-LocalUser -Name 'hh-owner'", xml, StringComparison.Ordinal);
            var credential = _credentials.Find(vmId)!;
            Assert.Equal("hhadmin", credential.UserName);
            Assert.DoesNotContain(credential.Password, xml, StringComparison.Ordinal);
        }

        var install = _installs.Find(vmId)!;
        Assert.Equal(UnattendedInstallState.Installing, install.State);
        Assert.Equal(_userId, install.UserId);
        Assert.Equal([(vmId, VmAction.Start)], _power.Calls);
        Assert.Equal(UnattendedSetup.BootKeyPresses, _keyboard.Presses.Count);
        Assert.All(_keyboard.Presses, press => Assert.Equal((vmId, UnattendedSetup.SpaceKey), press));
    }

    [Fact]
    public async Task Ubuntu_UsesTheUefiCaTemplateAndWaitsForConfirmationWithoutKeys()
    {
        var job = await RunAsync(Request("ubuntu.iso", new UnattendedInstallRequest("ubuntu-dev-server")));

        Assert.Equal(VmJobState.Succeeded, job.State);
        var blueprint = _builder.Configured!;
        Assert.Equal(UnattendedSetup.MicrosoftUefiCaTemplateId, blueprint.SecureBootTemplateId);
        using (var stream = File.OpenRead(blueprint.SeedIsoPath!))
        using (var reader = new CDReader(stream, joliet: true))
        {
            Assert.Equal("CIDATA", reader.VolumeLabel);
        }

        Assert.Equal(UnattendedInstallState.AwaitingConfirmation, _installs.Find(_builder.CreatedVmId)!.State);
        Assert.Single(_power.Calls);
        Assert.Empty(_keyboard.Presses);
    }

    [Fact]
    public async Task ManualLinuxInstall_StillGetsTheUefiCaTemplate()
    {
        var job = await RunAsync(Request("ubuntu.iso"));

        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.Equal(UnattendedSetup.MicrosoftUefiCaTemplateId, _builder.Configured!.SecureBootTemplateId);
        Assert.Null(_builder.Configured.SeedIsoPath);
        Assert.Empty(_power.Calls);
        Assert.Null(_installs.Find(_builder.CreatedVmId));
    }

    [Theory]
    [InlineData("ubuntu.iso", "windows-workstation", "install.profileId")]
    [InlineData("win11.iso", "ubuntu-dev-server", "install.profileId")]
    [InlineData("other.iso", "windows-workstation", "install.profileId")]
    [InlineData("win11.iso", "no-such-profile", "install.profileId")]
    public async Task MismatchedProfileOrImage_IsRejectedBeforeAnythingIsCreated(string iso, string profile, string field)
    {
        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _creation.StartAsync(_userId, Request(iso, new UnattendedInstallRequest(profile)), null, CancellationToken.None));

        Assert.Contains(error.Errors, issue => issue.Field == field);
        Assert.Empty(_builder.Steps);
    }

    [Fact]
    public async Task UnknownEdition_ListsTheEditionsTheImageHas()
    {
        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _creation.StartAsync(_userId, Request(install: new UnattendedInstallRequest("windows-workstation", "Windows 11 Enterprise")), null, CancellationToken.None));

        var issue = Assert.Single(error.Errors);
        Assert.Equal("install.windowsEdition", issue.Field);
        Assert.Contains("Windows 11 Home, Windows 11 Pro", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows11WithoutTpm_WarnsUnlessTheProfileSkipsHardwareChecks()
    {
        var warned = await Assert.ThrowsAsync<ResourceWarningsException>(() =>
            _creation.StartAsync(_userId, Request(install: new UnattendedInstallRequest("windows-workstation"), tpm: false), null, CancellationToken.None));
        Assert.Contains(warned.Warnings, warning => warning.Field == "enableTpm");

        var job = await RunAsync(Request(install: new UnattendedInstallRequest("windows-burner"), tpm: false));
        Assert.Equal(VmJobState.Succeeded, job.State);
    }

    [Fact]
    public async Task FailedJob_RemovesTheAnswerFileCredentialAndRecord()
    {
        _builder.FailAt = "notes";

        var job = await RunAsync(Request(install: new UnattendedInstallRequest("windows-workstation")));

        Assert.Equal(VmJobState.Failed, job.State);
        Assert.False(File.Exists(_builder.Configured!.SeedIsoPath));
        Assert.Null(_credentials.Find(_builder.CreatedVmId));
        Assert.Null(_installs.Find(_builder.CreatedVmId));
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task InvalidComputerName_IsRejected()
    {
        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _creation.StartAsync(_userId, Request(install: new UnattendedInstallRequest("windows-workstation", null, "far-too-long-computer-name")), null, CancellationToken.None));

        Assert.Contains(error.Errors, issue => issue.Field == "install.computerName");
    }

    [Fact]
    public async Task ASetupProfile_IsKeptWithTheInstall_AsItWasWhenTheVmWasCreated()
    {
        var stored = _setupProfiles.Create(_userId, new SetupProfile("Workstation", null, [new("7zip")], null, null, null));

        var job = await RunAsync(Request(install: new UnattendedInstallRequest("windows-workstation", SetupProfileId: stored.Id)));
        _setupProfiles.Update(_userId, stored.Id, new SetupProfile("Workstation", null, [new("vscode")], null, null, null));

        Assert.Equal(VmJobState.Succeeded, job.State);
        var install = _installs.Find(_builder.CreatedVmId)!;
        Assert.Equal("Workstation", install.SetupProfile!.Name);
        Assert.Equal("7zip", Assert.Single(install.SetupProfile.Install!).Id);
        Assert.False(install.AccountConfigured);
        Assert.Null(install.SetupResult);
    }

    [Theory]
    [InlineData("win11.iso", "windows-workstation", "missing-profile")]
    [InlineData("ubuntu.iso", "ubuntu-dev-server", null)]
    public async Task ASetupProfile_ThatIsMissingOrForLinux_IsRejected(string iso, string profile, string? setupProfileId)
    {
        setupProfileId ??= _setupProfiles.Create(_userId, new SetupProfile("Workstation", null, [new("7zip")], null, null, null)).Id;

        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _creation.StartAsync(_userId, Request(iso, new UnattendedInstallRequest(profile, SetupProfileId: setupProfileId)), null, CancellationToken.None));

        Assert.Contains(error.Errors, issue => issue.Field == "install.setupProfileId");
        Assert.Null(_builder.Configured);
    }
}
