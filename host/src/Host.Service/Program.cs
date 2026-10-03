using System.Security.Authentication;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Discovery;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Host.Service.Discovery;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Host.Service.Wake;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

// Elevated helper started by the tray after the user approves Wake-on-LAN fixes.
if (args.Length is 2 or 3 && args[0] == WakeFixHelper.Switch)
{
    return await WakeFixCommand.RunAsync(args[1], args.Length == 3 ? args[2] : null);
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
builder.Services.AddSingleton(new HostIdentityStore(dataDirectory));
builder.Services.AddSingleton(new HostCertificateStore(dataDirectory, Environment.MachineName));
var users = new UserStore(dataDirectory);
users.GetOrCreateDefault();
builder.Services.AddSingleton(users);
builder.Services.AddSingleton(new PairedDeviceStore(dataDirectory, users));

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

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// All interfaces, HTTPS only. Every endpoint except pairing requires a paired client certificate,
// which satisfies the CLAUDE.md rule against binding without mTLS.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var api = kestrel.ApplicationServices.GetRequiredService<IOptions<ApiOptions>>().Value;
    var certificate = kestrel.ApplicationServices.GetRequiredService<HostCertificateStore>().GetOrCreate();

    kestrel.ListenAnyIP(api.Port, listen => listen.UseHttps(https =>
    {
        https.ServerCertificate = certificate;
        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

        // Request a certificate but let unpaired clients connect so they can pair. Client
        // certificates are self-signed; PairedDeviceAuthenticationHandler checks the pinned fingerprint.
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.AllowAnyClientCertificate();
    }));
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
app.MapVmEndpoints();
app.MapPairingEndpoints();
app.MapWakeEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point type, exposed for integration tests.</summary>
public partial class Program;
