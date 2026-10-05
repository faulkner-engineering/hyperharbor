using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// HyperHarbor.Host.exe --self-test &lt;data copy&gt; &lt;result file&gt;: the check a new version passes before it is
/// installed (<see cref="SelfTestGate"/>). It starts the real host against the copy, on a free loopback port
/// with mDNS off, a private tray pipe, and no install watcher, so it changes nothing outside the copy. It then
/// reads every store, completes a TLS handshake with itself and checks the certificate it serves, reads the VM
/// inventory (read-only), stops, and writes a <see cref="SelfTestResult"/>. Exit code 0 means every check passed.
/// A failure before the host is built (an unreadable data format, invalid settings) ends the process with the
/// error on standard error and no result file; the gate reports that text.
/// </summary>
internal sealed class SelfTestRun(string resultFile, int port)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private readonly List<SelfTestCheck> _checks = [];

    /// <summary>Turns "--self-test copy result" into the host's settings for the run.</summary>
    public static (SelfTestRun Run, string[] HostArguments) Prepare(string[] args)
    {
        var dataCopy = Path.GetFullPath(args[1]);
        var port = FreeLoopbackPort();
        string[] hostArguments =
        [
            "--DataDirectory", dataCopy,
            "--Api:ListenAddress", IPAddress.Loopback.ToString(),
            "--Api:Port", port.ToString(CultureInfo.InvariantCulture),
            "--Discovery:Enabled", "false",
            "--Tray:PipeName", $"HyperHarbor.SelfTest.{Guid.NewGuid():N}",
        ];
        return (new SelfTestRun(Path.GetFullPath(args[2]), port), hostArguments);
    }

    public async Task<int> RunAsync(WebApplication app)
    {
        var services = app.Services;
        Check("dataFormat", () => $"Format {DataFormat.Read(services.GetRequiredService<IConfiguration>()["DataDirectory"]!)}");
        Check("stores", () => ReadStores(services));

        var started = await CheckAsync("start", async () =>
        {
            using var timeout = new CancellationTokenSource(StartTimeout);
            await app.StartAsync(timeout.Token);
            return $"Listening on 127.0.0.1:{port}";
        });

        if (started)
        {
            await CheckAsync("tls", () => HandshakeAsync(services.GetRequiredService<HostCertificateStore>()));
            await CheckAsync("inventory", async () =>
                $"{(await services.GetRequiredService<IVmInventory>().ListAsync(CancellationToken.None)).Count} VM(s)");
            await app.StopAsync();
        }

        var result = new SelfTestResult(HostVersion.Current.ToString(), _checks.TrueForAll(check => check.Passed), _checks);
        await File.WriteAllBytesAsync(resultFile, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, SelfTestResult.JsonOptions));
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>Reads each store, which loads (and for DPAPI files, decrypts) its file.</summary>
    private static string ReadStores(IServiceProvider services)
    {
        var user = services.GetRequiredService<UserStore>().GetOrCreateDefault();
        services.GetRequiredService<HostIdentityStore>().GetOrCreateHostId();
        var devices = services.GetRequiredService<PairedDeviceStore>().List().Count;
        _ = services.GetRequiredService<AdminPassphraseStore>().IsConfigured;
        services.GetRequiredService<VmCredentialStore>().Find(Guid.Empty);
        services.GetRequiredService<ProvisioningStore>().Find(Guid.Empty, user.UserId);
        services.GetRequiredService<ConsoleAccountStore>().List();
        services.GetRequiredService<UnattendedInstallStore>().List();
        services.GetRequiredService<UnattendProfileStore>().List(user.UserId);
        services.GetRequiredService<PerformanceStore>().List();
        _ = services.GetRequiredService<HostSettingsStore>().IsoFolder;
        return $"{devices} paired device(s)";
    }

    private async Task<string> HandshakeAsync(HostCertificateStore certificates)
    {
        using var expected = certificates.GetOrCreate();
        var protocol = await HostTlsCheck.HandshakeAsync(IPAddress.Loopback, port, expected, CancellationToken.None);
        return $"Served the host certificate over {protocol}";
    }

    private void Check(string name, Func<string> action)
    {
        try
        {
            _checks.Add(new SelfTestCheck(name, true, action()));
        }
        catch (Exception ex)
        {
            _checks.Add(new SelfTestCheck(name, false, ex.Message));
        }
    }

    private async Task<bool> CheckAsync(string name, Func<Task<string>> action)
    {
        try
        {
            _checks.Add(new SelfTestCheck(name, true, await action()));
            return true;
        }
        catch (Exception ex)
        {
            _checks.Add(new SelfTestCheck(name, false, ex.Message));
            return false;
        }
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
