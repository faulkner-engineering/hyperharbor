using HyperHarbor.Host.Core.GameAware;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary>Records collected on one host's stores and applied on another's.</summary>
public sealed class TransferRecordServiceTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid CopyId = Guid.Parse("9c8b7a65-4321-4fed-8cba-0987654321ab");
    private static readonly DateTimeOffset Time = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-records-tests", Guid.NewGuid().ToString("N"));
    private readonly Host _source;
    private readonly Host _target;

    public TransferRecordServiceTests()
    {
        _source = new Host(Path.Combine(_root, "source"));
        _target = new Host(Path.Combine(_root, "target"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Records_GoToTheVmsIdOnTheOtherHost_UnderTheUserWithTheSameAccount()
    {
        var sourceUser = _source.Users.GetOrCreateDefault();
        _source.Accounts.Save(new ProvisionedAccount(VmId, sourceUser.UserId, sourceUser.VmAccountName, Time, Time.AddDays(1)));
        _source.Credentials.Save(VmId, new GuestCredential("hhadmin", "Admin-Secret1!"));
        _source.GameAware.Update(settings => settings with
        {
            VmPolicies = new Dictionary<string, GameVmPolicy> { [VmId.ToString("D")] = new(GamePolicy.Save, AlwaysPause: true) },
        });
        var targetUser = _target.Users.GetOrCreateDefault();

        var records = _source.Service.Collect(VmId);
        var warnings = _target.Service.Apply(CopyId, records.ToWire().ToCore());

        Assert.Empty(warnings);
        var account = _target.Accounts.Find(CopyId, targetUser.UserId);
        Assert.NotNull(account);
        Assert.Equal((targetUser.VmAccountName, Time, Time.AddDays(1)), (account!.AccountName, account.ProvisionedAt, account.VerifiedAt));
        Assert.Equal(new GuestCredential("hhadmin", "Admin-Secret1!"), _target.Credentials.Find(CopyId));
        Assert.Equal(new GameVmPolicy(GamePolicy.Save, true), _target.GameAware.Load().VmPolicies[CopyId.ToString("D")]);
        Assert.Null(_target.Accounts.Find(VmId, targetUser.UserId));
    }

    [Fact]
    public void AnAccountOfAUserTheOtherHostDoesNotHave_IsAWarning()
    {
        _target.Users.GetOrCreateDefault();
        var records = new TransferRecords([new TransferAccountRecord("hh-somebody-else", GuestOsFamily.Windows, null, Time, Time)], null, null);

        var warnings = _target.Service.Apply(CopyId, records);

        Assert.Contains("hh-somebody-else", Assert.Single(warnings), StringComparison.Ordinal);
        Assert.False(_target.Accounts.AnyAccount(CopyId));
    }

    [Fact]
    public void AVmWithNothingRecorded_SendsEmptyRecords()
    {
        var records = _source.Service.Collect(VmId);

        Assert.Empty(records.Accounts);
        Assert.Null(records.AdminCredential);
        Assert.Null(records.GamePolicy);
    }

    [Fact]
    public void ToString_HidesTheCredential()
    {
        var records = new TransferRecords([], new GuestCredential("hhadmin", "Admin-Secret1!"), null);

        Assert.DoesNotContain("Admin-Secret1!", records.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Admin-Secret1!", records.ToWire().ToString(), StringComparison.Ordinal);
    }

    private sealed class Host
    {
        public Host(string data)
        {
            Directory.CreateDirectory(data);
            Accounts = new ProvisioningStore(data);
            Credentials = new VmCredentialStore(data);
            GameAware = new GameAwareStore(data);
            Users = new UserStore(data);
            Service = new TransferRecordService(Accounts, Credentials, GameAware, Users, NullLogger<TransferRecordService>.Instance);
        }

        public ProvisioningStore Accounts { get; }

        public VmCredentialStore Credentials { get; }

        public GameAwareStore GameAware { get; }

        public UserStore Users { get; }

        public TransferRecordService Service { get; }
    }
}
