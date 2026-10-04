using System.Net;
using System.Security.Authentication;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Discovery;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Host.Service.Discovery;
using HyperHarbor.Host.Service.Logging;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Host.Service.VmConsole;
using HyperHarbor.Host.Service.Wake;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

// Elevated helper started by the tray after the user approves Wake-on-LAN fixes.
if (args.Length is 2 or 3 && args[0] == WakeFixHelper.Switch)
{
    return await WakeFixCommand.RunAsync(args[1], args.Length == 3 ? args[2] : null);
}

// Elevated helper that creates or removes the host console accounts (tray or Start-HyperHarbor.ps1).
if (ConsoleSetupCommand.Matches(args))
{
    return await ConsoleSetupCommand.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

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
    options.ServiceName = "HyperHarbor Host";
});
builder.Services.AddHostedService<VmInventoryStartupLogger>();

// Identity, certificate, and paired devices live under %ProgramData%\HyperHarbor unless DataDirectory is set.
var dataDirectory = builder.Configuration["DataDirectory"] is { Length: > 0 } configured
    ? configured
    : HostIdentityStore.DefaultDataDirectory;
// The console and the daily log file under <data>ogs get the same entries.
// Registered through DI so the container disposes it, which closes the file when the service stops.
builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(dataDirectory));
builder.Services.AddSingleton(new HostIdentityStore(dataDirectory));
builder.Services.AddSingleton(new HostCertificateStore(dataDirectory, Environment.MachineName));
var users = new UserStore(dataDirectory);
users.GetOrCreateDefault();
builder.Services.AddSingleton(users);
builder.Services.AddSingleton(new PairedDeviceStore(dataDirectory, users));
builder.Services.AddSingleton<IAuditLog>(new FileAuditLog(dataDirectory));
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
    trayPipeName));
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
builder.Services.AddOptions<LifecycleOptions>().Bind(builder.Configuration.GetSection(LifecycleOptions.SectionName));
builder.Services.AddSingleton<IHostCapacityReader, WindowsHostCapacityReader>();
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
builder.Services.AddSingleton(services => ActivatorUtilities.CreateInstance<VmCreationService>(services, services.GetRequiredService<VmStorageLocation>()));
builder.Services.AddSingleton<IHyperVCompute, CimHyperVCompute>();
builder.Services.AddSingleton<VmComputeService>();

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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHostEndpoints();
app.MapAuthEndpoints();
app.MapVmEndpoints();
app.MapConsoleEndpoints();
app.MapUnattendEndpoints();
app.MapJobEndpoints();
app.MapPairingEndpoints();
app.MapWakeEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point type, exposed for integration tests.</summary>
public partial class Program;
