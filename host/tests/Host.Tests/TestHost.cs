using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service.Wake;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Host.Tests.VmConsole;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Tests;

/// <summary>
/// The host API running in memory with fake Hyper-V, a fake tray, and a temporary data directory.
/// TestServer has no TLS, so a test-only middleware takes the client certificate from a header.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public const string ClientCertificateHeader = "X-Test-Client-Certificate";

    private readonly WebApplicationFactory<Program> _factory;

    /// <param name="configureServices">Replaces further services after the defaults, for example a failing audit log.</param>
    public TestHost(Action<IServiceCollection>? configureServices = null)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
        Invoker = new FakePowerInvoker(Inventory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Discovery:Enabled", "false")
            .UseSetting("DataDirectory", DataDirectory)
            .UseSetting("Lifecycle:IsoFolder", Path.Combine(DataDirectory, "isos"))
            .UseSetting("Tray:PipeName", "HyperHarbor.Tests." + Guid.NewGuid().ToString("N"))
            .ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(category: null, LogLevel.Trace))
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IVmInventory>(Inventory);
                services.AddSingleton<IHyperVPowerInvoker>(Invoker);
                services.AddSingleton<IPairingNotifier>(Tray);
                services.AddSingleton<IWakeFixApprover>(Tray);
                services.AddSingleton<IWakeEnvironmentReader>(Wake);
                services.AddSingleton<ISleepController>(Sleep);
                services.AddSingleton<IGuestAccountManager>(Guest);
                services.AddSingleton<IHyperVStorage>(Storage);
                services.AddSingleton<IDiskFiles>(DiskFiles);
                services.AddSingleton<IHostCapacityReader>(Capacity);
                services.AddSingleton<IHyperVHost>(HyperVHost);
                services.AddSingleton<IHyperVBuilder>(Builder);
                services.AddSingleton<IHyperVCompute>(Compute);
                services.AddSingleton<IConsolePasswordChanger>(ConsolePasswords);
                services.AddSingleton<IConsoleAccessGranter>(ConsoleAccess);
                services.AddSingleton<Core.Unattend.IVmKeyboard>(Keyboard);
                services.AddSingleton<Core.Unattend.IVmMedia>(Media);
                services.AddSingleton<Core.Performance.IHyperVPerformance>(Performance);
                services.AddSingleton<Core.Performance.IHostGpuReader>(HostGpus);
                services.AddSingleton<Core.Unattend.IRemoteAccessProbe>(RemoteAccess);
                services.AddSingleton(provider => new Core.Unattend.UnattendedSetup(
                    provider.GetRequiredService<Core.Unattend.UnattendProfileStore>(),
                    provider.GetRequiredService<Core.Unattend.IsoInspector>(),
                    provider.GetRequiredService<UserStore>(),
                    provider.GetRequiredService<VmCredentialStore>(),
                    provider.GetRequiredService<Core.Unattend.UnattendedInstallStore>(),
                    provider.GetRequiredService<IHyperVPowerInvoker>(),
                    Keyboard,
                    TimeProvider.System,
                    provider.GetRequiredService<ILogger<Core.Unattend.UnattendedSetup>>(),
                    keyInterval: TimeSpan.Zero));
                services.AddSingleton<IStartupFilter, ClientCertificateFromHeader>();
                configureServices?.Invoke(services);
            }));
    }

    public string DataDirectory { get; }

    public FakeVmInventory Inventory { get; } = new();

    public FakePowerInvoker Invoker { get; }

    public FakeTray Tray { get; } = new();

    public FakeWakeEnvironment Wake { get; } = new();

    public FakeSleepController Sleep { get; } = new();

    public FakeGuestAccountManager Guest { get; } = new();

    public FakeHyperVStorage Storage { get; } = new();

    public FakeDiskFiles DiskFiles { get; } = new();

    public FakeHostCapacity Capacity { get; } = new();

    public FakeHyperVHost HyperVHost { get; } = new();

    public FakeHyperVBuilder Builder { get; } = new();

    public FakeHyperVCompute Compute { get; } = new();

    public FakeConsolePasswordChanger ConsolePasswords { get; } = new();

    public FakeConsoleAccess ConsoleAccess { get; } = new();

    public Unattend.FakeVmKeyboard Keyboard { get; } = new();

    public Unattend.FakeVmMedia Media { get; } = new();

    public Performance.FakeHyperVPerformance Performance { get; } = new();

    public Performance.FakeHostGpuReader HostGpus { get; } = new();

    public Unattend.FakeRemoteAccessProbe RemoteAccess { get; } = new();

    /// <summary>The ISO library folder, inside the data directory.</summary>
    public string IsoFolder => Path.Combine(DataDirectory, "isos");

    /// <summary>Entries in the host's audit.log, oldest first.</summary>
    public IReadOnlyList<System.Text.Json.Nodes.JsonObject> AuditEntries()
    {
        var path = Path.Combine(DataDirectory, Core.Audit.FileAuditLog.FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject())
            .ToList();
    }

    /// <summary>Every log entry the host wrote, at every level.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public IServiceProvider Services => _factory.Services;

    /// <summary>A client that presents no certificate.</summary>
    public HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>A client that presents <paramref name="certificate"/> as its TLS client certificate.</summary>
    public HttpClient CreateClient(X509Certificate2 certificate)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientCertificateHeader, Convert.ToBase64String(certificate.RawData));
        return client;
    }

    /// <summary>Does what the elevated setup command does for the default User, with a fake account.</summary>
    public ConsoleCredential SetUpConsoleAccount(string password = "Console-Initial1!")
    {
        var user = Services.GetRequiredService<UserStore>().GetOrCreateDefault();
        var credential = new ConsoleCredential(ConsoleAccountName.For(user.Name), password);
        ConsolePasswords.Passwords[credential.AccountName] = password;
        Services.GetRequiredService<ConsoleAccountStore>().Save(user.UserId, credential);
        return credential;
    }

    /// <summary>Adds <paramref name="certificate"/> to the paired device store directly.</summary>
    public PairedDevice Pair(X509Certificate2 certificate, string name = "Test Device") =>
        Services.GetRequiredService<PairedDeviceStore>().Add(
            Services.GetRequiredService<UserStore>().GetOrCreateDefault().UserId,
            name,
            CertificateFingerprint.Of(certificate),
            DateTimeOffset.UtcNow);

    public void Dispose()
    {
        _factory.Dispose();

        // A file the host just closed (the log) can stay open briefly while antivirus scans it.
        for (var attempt = 1; Directory.Exists(DataDirectory); attempt++)
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static X509Certificate2 CreateClientCertificate(string name = "Test Client")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private sealed class ClientCertificateFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ClientCertificateHeader, out var value))
                {
                    context.Connection.ClientCertificate = new X509Certificate2(Convert.FromBase64String(value.ToString()));
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>Captures what the tray would display.</summary>
internal sealed class FakeTray : IPairingNotifier, IWakeFixApprover
{
    public bool CanDisplayPin { get; set; } = true;

    public string? Pin { get; private set; }

    public List<PairingOutcome> Outcomes { get; } = [];

    public void PairingStarted(Guid pairingId, string deviceName, string pin, DateTimeOffset expiresAt) => Pin = pin;

    public void PairingEnded(Guid pairingId, string deviceName, PairingOutcome outcome) => Outcomes.Add(outcome);

    public bool CanRequestApproval => CanDisplayPin;

    public List<WakeFixRequestedMessage> WakeFixRequests { get; } = [];

    public void RequestApproval(WakeFixRequestedMessage request) => WakeFixRequests.Add(request);
}

/// <summary>Client side of SPAKE2, used by tests to act as a pairing client.</summary>
internal static class TestPairingClient
{
    public sealed record Exchange(PairingConfirmation Confirmation, byte[] ExpectedHostConfirmation);

    public static Exchange Compute(
        Guid pairingId,
        string pin,
        byte[] hostShare,
        X509Certificate2 clientCertificate,
        byte[] hostCertificateDer)
    {
        var w = Spake2.PasswordScalar(pairingId, pin);
        var x = Spake2.RandomScalar();
        var bigX = Spake2.ComputeShare(Spake2.Role.Client, x, w);
        var bigY = Spake2.TryDecodeShare(hostShare) ?? throw new InvalidOperationException("Invalid host share.");
        BigInteger k = Spake2.ComputeSharedElement(Spake2.Role.Client, x, w, bigY);

        var confirmations = Spake2.ComputeConfirmations(
            pairingId,
            SHA256.HashData(clientCertificate.RawData),
            SHA256.HashData(hostCertificateDer),
            bigX,
            bigY,
            k,
            w);

        return new Exchange(new PairingConfirmation(Spake2.Encode(bigX), confirmations.Client), confirmations.Host);
    }
}
