using HyperHarbor.Host.Core;
using HyperHarbor.Host.Service;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Shared.Contracts;
using Microsoft.Extensions.Options;

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

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Loopback only. Binding to other interfaces must wait for mTLS (see CLAUDE.md).
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var api = kestrel.ApplicationServices.GetRequiredService<IOptions<ApiOptions>>().Value;
    kestrel.ListenLocalhost(api.Port);
});

builder.Services.ConfigureHttpJsonOptions(options => ContractJson.Configure(options.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

await using var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapVmEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point type, exposed for integration tests.</summary>
public partial class Program;
