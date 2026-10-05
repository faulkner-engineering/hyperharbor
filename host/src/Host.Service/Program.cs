using System.Net;
using System.Security.Authentication;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Discovery;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.RemoteDesktop;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Host.Service.Discovery;
using HyperHarbor.Host.Service.Installation;
using HyperHarbor.Host.Service.Logging;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Host.Service.VmConsole;
using HyperHarbor.Host.Service.Wake;
using HyperHarbor.Host.Tray;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

// HyperHarbor.Host.exe is the service, the tray, the installer, and the elevated helpers (HostCommandLine).
var isWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
SelfTestRun? selfTest = null;
if (!isWindowsService)
{
    // A Windows executable has no console: commands that print attach to the terminal that started them.
    var redirected = ConsoleAttachment.HasStandardOutput;
    var attached = !redirected && ConsoleAttachment.AttachToParent();
    (var mode, args) = HostCommandLine.Parse(args, startedFromConsole: redirected || attached);
    if (attached && !HostCommandLine.WritesToConsole(mode))
    {
        ConsoleAttachment.Detach();
    }

    switch (mode)
    {
        case HostMode.Tray:
            return TrayApp.Run();
        case HostMode.Launcher:
            return await Launcher.RunAsync();
        case HostMode.Install:
            return await InstallCommand.InstallAsync(args, attached || redirected ? new ConsoleInstallUi() : new DialogInstallUi());
        case HostMode.Uninstall:
            return await InstallCommand.UninstallAsync(args, attached || redirected ? new ConsoleInstallUi() : new DialogInstallUi());
        case HostMode.SaveWakeDiagnostics:
            return WakeDiagnosticsCommand.Run(args);
        case HostMode.Help:
            Console.WriteLine(HostCommandLine.Usage);
            return 0;
        case HostMode.ApplyWakeFixes:
            // Elevated helper started by the tray after the user approves Wake-on-LAN fixes.
            return await WakeFixCommand.RunAsync(args[1], args.Length == 3 ? args[2] : null);
        case HostMode.ConsoleSetup:
            // Elevated helper that creates or removes the host console accounts (tray or the installer).
            return await ConsoleSetupCommand.RunAsync(args);
        case HostMode.WriteUpdateManifest:
            return WriteManifestCommand.Run(args);
        case HostMode.UpdateRun:
            return await UpdateRunCommand.RunAsync();
        case HostMode.SelfTest:
            // The gate a new version passes before it is installed: the real host against a copy of the data.
            (selfTest, args) = SelfTestRun.Prepare(args);
            break;
        case HostMode.Host when !redirected:
            // A console run gets its own window, which outlives the terminal or script that started it.
            ConsoleAttachment.OpenWindow();
            break;
    }
}

// A service starts in System32, so appsettings.json is read from the executable's folder instead.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = isWindowsService ? AppContext.BaseDirectory : null,
});

// The defaults travel inside the executable, below every other source (appsettings.json beside it, the command line).
builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Json.JsonStreamConfigurationSource
{
    Stream = typeof(HostCommandLine).Assembly.GetManifestResourceStream("HyperHarbor.Host.appsettings.json")
        ?? throw new InvalidOperationException("The default settings are missing from the executable."),
});

builder.Services.AddVmInventory();
builder.Services.AddVmPowerControl();

if (args.Contains(ListVmsCommand.Switch, StringComparer.OrdinalIgnoreCase))
{
    // Keep stdout limited to the JSON output.
    builder.Logging.ClearProviders();
    await using var diagnosticApp = builder.Build();
    return await ListVmsCommand.RunAsync(diagnosticApp.Services);
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = HostService.Name;
});
if (isWindowsService)
{
    // Replaces the lifetime AddWindowsService registered, to handle SERVICE_CONTROL_PRESHUTDOWN.
    builder.Services.AddSingleton<IHostLifetime, PreshutdownServiceLifetime>();
}

builder.Services.AddHostedService<VmInventoryStartupLogger>();

// Identity, certificate, and paired devices live under %ProgramData%\HyperHarbor unless DataDirectory is set.
var dataDirectory = builder.Configuration["DataDirectory"] is { Length: > 0 } configured
    ? configured
    : HostIdentityStore.DefaultDataDirectory;

// The installed service runs as LocalSystem; the user who installed it keeps the tray and read access to the logs.
var trayUser = TrayUser.Resolve(builder.Configuration, isWindowsService);
IReadOnlyCollection<System.Security.Principal.SecurityIdentifier> logReaders = trayUser is null ? [] : [trayUser];

// Other local accounts must not be able to plant files the service would trust (the installer does this too).
var untrustedDataEntries = isWindowsService ? DataDirectoryAcl.Secure(dataDirectory, trayUser) : [];

// Migrates older data and refuses data a newer version wrote, before any store reads it.
DataFormat.EnsureCurrent(dataDirectory);

// The console and the daily log file under <data>\logs get the same entries.
// Registered through DI so the container disposes it, which closes the file when the service stops.
builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(dataDirectory, readers: logReaders));
builder.Services.AddSingleton(new HostIdentityStore(dataDirectory));
builder.Services.AddSingleton(new HostCertificateStore(dataDirectory, Environment.MachineName));
var users = new UserStore(dataDirectory);
users.GetOrCreateDefault();
builder.Services.AddSingleton(users);
builder.Services.AddSingleton(new PairedDeviceStore(dataDirectory, users));
builder.Services.AddSingleton<IAuditLog>(new FileAuditLog(dataDirectory, readers: logReaders));
builder.Services.AddSingleton(new AdminPassphraseStore(dataDirectory));
builder.Services.AddSingleton(services => new ElevationService(
    services.GetRequiredService<AdminPassphraseStore>(),
    services.GetRequiredService<PairedDeviceStore>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<ElevationService>>(),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Elevation:TokenLifetimeSeconds", (int)ElevationService.DefaultTokenLifetime.TotalSeconds))));

builder.Services.AddOptions<DiscoveryOptions>().Bind(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
builder.Services.AddSingleton<IServiceAdvertiser, WindowsDnsServiceAdvertiser>();
builder.Services.AddHostedService<DiscoveryAdvertisementService>();

// The tray pipe server shows pairing PINs, so it is also the pairing notifier.
// Tray:PipeName exists so tests can run beside a real tray without either connecting to the other.
var trayPipeName = builder.Configuration["Tray:PipeName"] is { Length: > 0 } pipeName ? pipeName : TrayPipe.Name;
builder.Services.AddSingleton(services => new TrayPipeServer(
    services.GetRequiredService<PairedDeviceStore>(),
    services,
    services.GetRequiredService<ILogger<TrayPipeServer>>(),
    trayPipeName,
    trayUser));
builder.Services.AddSingleton<IPairingNotifier>(services => services.GetRequiredService<TrayPipeServer>());
builder.Services.AddHostedService(services => services.GetRequiredService<TrayPipeServer>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PairingService>();

builder.Services.AddSingleton<IWakeEnvironmentReader, WindowsWakeEnvironmentReader>();
builder.Services.AddSingleton<ISleepController, WindowsSleepController>();
builder.Services.AddSingleton<WakeTestScheduler>();
builder.Services.AddSingleton<IWakeFixApprover>(services => services.GetRequiredService<TrayPipeServer>());
builder.Services.AddSingleton<WakeFixCoordinator>();

builder.Services.AddSingleton(new VmCredentialStore(dataDirectory));
builder.Services.AddSingleton(new ProvisioningStore(dataDirectory));
builder.Services.AddSingleton<IGuestAccountManager>(new GuestAccountRouter(new PowerShellDirectAccountManager(), new SshAccountManager()));
builder.Services.AddSingleton<ProvisioningService>();
builder.Services.AddSingleton(services => new PasswordRotator(
    services.GetRequiredService<IGuestAccountManager>(),
    services.GetRequiredService<VmCredentialStore>(),
    services.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Rdp:ReuseWindowSeconds", (int)PasswordRotator.DefaultReuseWindow.TotalSeconds)),
    services.GetRequiredService<ILogger<PasswordRotator>>()));
builder.Services.AddSingleton<ConnectService>();

builder.Services.AddOptions<ConsoleOptions>()
    .Bind(builder.Configuration.GetSection(ConsoleOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => System.Net.IPEndPoint.TryParse(options.Endpoint, out _), "Console:Endpoint must be an IP address and port.")
    .ValidateOnStart();
builder.Services.AddSingleton(new ConsoleAccountStore(dataDirectory));
builder.Services.AddSingleton<IConsolePasswordChanger, WindowsLocalAccounts>();
builder.Services.AddSingleton<IConsoleAccessGranter, CimConsoleAccess>();
builder.Services.AddSingleton(services => new ConsolePasswordRotator(
    services.GetRequiredService<ConsoleAccountStore>(),
    services.GetRequiredService<IConsolePasswordChanger>(),
    services.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(services.GetRequiredService<IOptions<ConsoleOptions>>().Value.ReuseWindowSeconds),
    services.GetRequiredService<ILogger<ConsolePasswordRotator>>()));
builder.Services.AddSingleton(services => new ConsoleTicketStore(
    services.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(services.GetRequiredService<IOptions<ConsoleOptions>>().Value.TicketLifetimeSeconds),
    services.GetRequiredService<PairedDeviceStore>()));
builder.Services.AddSingleton<ConsoleService>();

builder.Services.AddSingleton<IHyperVStorage, CimHyperVStorage>();
builder.Services.AddSingleton<IDiskFiles, WindowsDiskFiles>();
builder.Services.AddSingleton(services => new VmJobStore(
    services.GetRequiredService<VmOperationLocks>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<VmJobStore>>()));
builder.Services.AddSingleton<VmDeletionService>();
builder.Services.AddSingleton<IDiskCopier, FileDiskCopier>();
builder.Services.AddSingleton<BackupLocation>();
builder.Services.AddSingleton<VmDiskExportService>();
builder.Services.AddOptions<LifecycleOptions>().Bind(builder.Configuration.GetSection(LifecycleOptions.SectionName));
builder.Services.AddSingleton<IHostCapacityReader, WindowsHostCapacityReader>();
builder.Services.AddSingleton<IRemoteDesktopSettings, WindowsRemoteDesktopSettings>();
builder.Services.AddSingleton<HostRemoteDesktopService>();
builder.Services.AddSingleton<IHyperVHost, CimHyperVHost>();
builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<LifecycleOptions>>().Value);
// The tray can move the ISO library; its choice (host-settings.json) wins over Lifecycle:IsoFolder and the default.
builder.Services.AddSingleton(new HostSettingsStore(dataDirectory));
builder.Services.AddSingleton(services =>
{
    var settings = services.GetRequiredService<HostSettingsStore>();
    var options = services.GetRequiredService<LifecycleOptions>();
    return new IsoLibrary(() => settings.IsoFolder ?? options.EffectiveIsoFolder, services.GetRequiredService<ILogger<IsoLibrary>>());
});
builder.Services.AddSingleton<IsoLibraryService>();
builder.Services.AddSingleton<IHyperVBuilder, CimHyperVBuilder>();
builder.Services.AddSingleton(services => new VmStorageLocation(
    services.GetRequiredService<HostSettingsStore>(),
    services.GetRequiredService<LifecycleOptions>(),
    services.GetRequiredService<IHyperVHost>()));
// Unattended installs: profiles, ISO inspection, answer files, and the install records the readiness watcher follows.
builder.Services.AddSingleton(new UnattendProfileStore(dataDirectory));
builder.Services.AddSingleton<IsoInspector>();
builder.Services.AddSingleton(new UnattendedInstallStore(dataDirectory));
builder.Services.AddSingleton<IVmKeyboard, CimVmKeyboard>();
builder.Services.AddSingleton(services => ActivatorUtilities.CreateInstance<UnattendedSetup>(services));
builder.Services.AddSingleton<IRemoteAccessProbe>(new TcpRemoteAccessProbe());
builder.Services.AddSingleton<IVmMedia, CimVmMedia>();
builder.Services.AddSingleton(builder.Configuration.GetSection(InstallWatcherOptions.SectionName).Get<InstallWatcherOptions>() ?? new InstallWatcherOptions());
builder.Services.AddSingleton<UnattendedInstallWatcher>();
if (selfTest is null)
{
    // The watcher changes VMs (passwords, seed media), so a self-test run leaves it out.
    builder.Services.AddHostedService<InstallWatcherService>();
}

// What an update restart would interrupt; the update gate middleware counts state-changing requests here.
builder.Services.AddSingleton(services => new HostActivity(
    () => services.GetRequiredService<VmJobStore>().AnyRunning || services.GetRequiredService<PairingService>().HasPendingRequest,
    services.GetRequiredService<TimeProvider>()));

// Keeps the PC awake while paired devices use it (after a Wake-on-LAN wake Windows would sleep again in minutes).
builder.Services.AddSingleton(builder.Configuration.GetSection(KeepAwakeOptions.SectionName).Get<KeepAwakeOptions>() ?? new KeepAwakeOptions());
builder.Services.AddSingleton<RemoteUseTracker>();
builder.Services.AddSingleton<IPowerRequest, WindowsPowerRequest>();
builder.Services.AddSingleton<IRemoteSessions, WindowsRemoteSessions>();
builder.Services.AddSingleton<KeepAwakeController>();
if (selfTest is null)
{
    // A self-test run on a copy of the data must not hold the real host awake.
    builder.Services.AddHostedService<KeepAwakeService>();
}

// Update settings: the Update section, with the owner's choices from host-settings.json applied.
builder.Services.AddSingleton(builder.Configuration.GetSection(UpdateOptions.SectionName).Get<UpdateOptions>() ?? new UpdateOptions());
builder.Services.AddSingleton(services => new UpdateSettings(services.GetRequiredService<UpdateOptions>(), services.GetRequiredService<HostSettingsStore>()));

if (isWindowsService)
{
    // update\health.json, which the update helper waits for after starting a new version of the service.
    builder.Services.AddHostedService<HealthReporter>();

    // Updates apply to the installed service only: checks, downloads, self-tests, and the handoff to the helper.
    builder.Services.AddSingleton(services =>
    {
        var options = services.GetRequiredService<UpdateOptions>();
        var settings = services.GetRequiredService<UpdateSettings>();
        var downloader = new UpdateDownloader(UpdateDownloader.CreateHttpClient($"HyperHarbor-Host/{HostVersion.Current}"), options);
        var gate = new SelfTestGate(dataDirectory, new SelfTestProcess(), SelfTestGate.DefaultTimeout);
        var preparer = new UpdatePreparer(options, downloader, new UnsignedPackageVerifier(), gate, dataDirectory);
        return new UpdateCoordinator(
            preparer,
            new UpdateStateStore(dataDirectory),
            services.GetRequiredService<HostActivity>(),
            settings.Current,
            HostVersion.Current,
            UpdateTask.Run,
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<UpdateCoordinator>>());
    });
    builder.Services.AddHostedService<UpdateService>();
}

builder.Services.AddSingleton(services => ActivatorUtilities.CreateInstance<VmCreationService>(services, services.GetRequiredService<VmStorageLocation>()));
builder.Services.AddSingleton<IHyperVCompute, CimHyperVCompute>();
builder.Services.AddSingleton<VmComputeService>();

// Performance mode: GPU partitioning and fixed resources for Windows VMs.
builder.Services.AddSingleton(new PerformanceStore(dataDirectory));
builder.Services.AddSingleton<IHyperVPerformance, CimHyperVPerformance>();
builder.Services.AddSingleton<IHostGpuReader, CimHostGpuReader>();
builder.Services.AddSingleton<IGpuEventSource, EventLogGpuEventSource>();
builder.Services.AddSingleton<GpuEventReader>();
builder.Services.AddSingleton<IGpuDriverSource, CimGpuDriverSource>();
builder.Services.AddSingleton<IGuestPerformanceSetup, PowerShellDirectPerformanceSetup>();
builder.Services.AddSingleton<VmPerformanceService>();
builder.Services.AddSingleton<GpuVmShutdownCoordinator>();

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// All interfaces (or Api:ListenAddress), HTTPS only. Every endpoint except pairing requires a paired client certificate,
// which satisfies the CLAUDE.md rule against binding without mTLS.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var api = kestrel.ApplicationServices.GetRequiredService<IOptions<ApiOptions>>().Value;
    var certificate = kestrel.ApplicationServices.GetRequiredService<HostCertificateStore>().GetOrCreate();

    void Https(ListenOptions listen) => listen.UseHttps(https =>
    {
        https.ServerCertificate = certificate;
        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

        // Request a certificate but let unpaired clients connect so they can pair. Client
        // certificates are self-signed; PairedDeviceAuthenticationHandler checks the pinned fingerprint.
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.AllowAnyClientCertificate();
    });

    if (string.IsNullOrEmpty(api.ListenAddress))
    {
        kestrel.ListenAnyIP(api.Port, Https);
    }
    else
    {
        kestrel.Listen(IPAddress.Parse(api.ListenAddress), api.Port, Https);
    }
});

builder.Services
    .AddAuthentication(PairedDeviceAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PairedDeviceAuthenticationHandler>(PairedDeviceAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder(PairedDeviceAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.ConfigureHttpJsonOptions(options => ContractJson.Configure(options.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

await using var app = builder.Build();

foreach (var removed in untrustedDataEntries)
{
    app.Logger.LogWarning("Removed {Path} from the data directory: an untrusted account owned it.", removed);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseUpdateGate();
app.UseAuthentication();
app.UseAuthorization();
app.UseRemoteUseTracking();

app.MapHostEndpoints();
app.MapAuthEndpoints();
app.MapVmEndpoints();
app.MapConsoleEndpoints();
app.MapUnattendEndpoints();
app.MapPerformanceEndpoints();
app.MapJobEndpoints();
app.MapPairingEndpoints();
app.MapWakeEndpoints();
app.MapUpdateEndpoints();

if (selfTest is not null)
{
    return await selfTest.RunAsync(app);
}

await app.RunAsync();
return 0;

/// <summary>Entry point type, exposed for integration tests.</summary>
public partial class Program;
