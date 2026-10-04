using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Host.Service.Api;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Tests.VmConsole;

/// <summary>
/// Runs the tunnel handler on real Kestrel, because TestServer cannot upgrade connections, with an
/// echo server standing in for the host's Virtual Machine Connection service.
/// </summary>
public sealed class ConsoleTunnelTests : IAsyncLifetime
{
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid VmId = Guid.NewGuid();

    private readonly ConsoleTicketStore _tickets = new(TimeProvider.System, TimeSpan.FromSeconds(120));
    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<long> _echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private string _vmEndpoint = string.Empty;
    private int _port;

    public async Task InitializeAsync()
    {
        _echo.Start();
        _vmEndpoint = _echo.LocalEndpoint.ToString()!;
        _ = EchoOnceAsync();
        await StartAsync(() => _vmEndpoint);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _echo.Stop();
    }

    [Fact]
    public async Task ValidTicket_UpgradesAndCopiesBytesBothWays()
    {
        var ticket = _tickets.Issue(DeviceId, UserId, VmId);
        using var client = await ConnectAsync(VmId, ticket.Value);
        var stream = client.GetStream();

        var head = await ReadHeadAsync(stream);
        Assert.StartsWith("HTTP/1.1 101", head, StringComparison.Ordinal);
        Assert.Contains($"Upgrade: {ContractInfo.ConsoleUpgradeProtocol}", head, StringComparison.OrdinalIgnoreCase);

        var payload = Encoding.ASCII.GetBytes("remote desktop bytes");
        await stream.WriteAsync(payload);
        var echoed = new byte[payload.Length];
        await stream.ReadExactlyAsync(echoed);
        Assert.Equal(payload, echoed);

        // Closing the client ends the tunnel, which closes the console service connection too.
        client.Close();
        Assert.Equal(payload.Length, await _echoed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task TicketForAnotherVm_IsRejectedBeforeUpgrading()
    {
        var ticket = _tickets.Issue(DeviceId, UserId, Guid.NewGuid());
        using var client = await ConnectAsync(VmId, ticket.Value);

        var head = await ReadHeadAsync(client.GetStream());

        Assert.StartsWith("HTTP/1.1 403", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsoleServiceDown_Returns502WithoutUpgrading()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        _vmEndpoint = closed.LocalEndpoint.ToString()!;
        closed.Stop();

        var ticket = _tickets.Issue(DeviceId, UserId, VmId);
        using var client = await ConnectAsync(VmId, ticket.Value);

        var head = await ReadHeadAsync(client.GetStream());

        Assert.StartsWith("HTTP/1.1 502", head, StringComparison.Ordinal);
    }

    private async Task StartAsync(Func<string> vmEndpoint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_tickets);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IOptions<ConsoleOptions>>(_ => new DynamicOptions(vmEndpoint));
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();

        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(PairedDeviceAuthenticationHandler.DeviceIdClaim, DeviceId.ToString("D")),
                    new Claim(PairedDeviceAuthenticationHandler.UserIdClaim, UserId.ToString("D")),
                ],
                "Test"));
            return next(context);
        });
        _app.MapPost(VmEndpoints.BasePath + "/{vmId:guid}/console/tunnel", ConsoleEndpoints.TunnelAsync);
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _port = new Uri(address).Port;
    }

    private async Task<TcpClient> ConnectAsync(Guid vmId, string ticket)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var request =
            $"POST {VmEndpoints.BasePath}/{vmId}/console/tunnel HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Connection: Upgrade\r\n" +
            $"Upgrade: {ContractInfo.ConsoleUpgradeProtocol}\r\n" +
            $"{ContractInfo.ConsoleTicketHeader}: {ticket}\r\n" +
            "Content-Length: 0\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
        return client;
    }

    /// <summary>Reads the response status line and headers, byte by byte so no tunnel bytes are consumed.</summary>
    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, timeout.Token) == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }

    private async Task EchoOnceAsync()
    {
        try
        {
            using var connection = await _echo.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            var buffer = new byte[4096];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
                total += read;
            }

            _echoed.TrySetResult(total);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _echoed.TrySetException(ex);
        }
    }

    private sealed class DynamicOptions(Func<string> endpoint) : IOptions<ConsoleOptions>
    {
        public ConsoleOptions Value => new() { Endpoint = endpoint() };
    }
}
