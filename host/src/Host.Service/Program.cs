using HyperHarbor.Host.Service;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "HyperHarbor Host";
});
builder.Services.AddHostedService<HostLifetimeLogger>();

var app = builder.Build();
app.Run();
