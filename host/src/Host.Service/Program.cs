using HyperHarbor.Host.Core;
using HyperHarbor.Host.Service;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

builder.Services.AddVmInventory();

if (args.Contains(ListVmsCommand.Switch, StringComparer.OrdinalIgnoreCase))
{
    // Keep stdout limited to the JSON output.
    builder.Logging.ClearProviders();
    using var diagnosticHost = builder.Build();
    return await ListVmsCommand.RunAsync(diagnosticHost.Services);
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "HyperHarbor Host";
});
builder.Services.AddHostedService<VmInventoryStartupLogger>();

using var app = builder.Build();
await app.RunAsync();
return 0;
