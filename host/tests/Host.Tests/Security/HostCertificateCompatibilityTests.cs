using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Tests.Security;

/// <summary>
/// The host certificate is the identity every paired client pins, and a host that cannot load it would make every
/// client pair again. These tests load a certificate written by a .NET 8 host (0.2.x) with the current loader, so a
/// runtime or loader change that rejects existing files fails here rather than on installed hosts.
/// </summary>
public sealed class HostCertificateCompatibilityTests : IDisposable
{
    /// <summary>
    /// The PKCS#12 bytes HostCertificateStore.Create wrote on .NET 8.0.33 (2026-11-05) for the host name FIXTURE-HOST,
    /// before DPAPI protection. A test-only key: it is used nowhere else.
    /// </summary>
    private const string Net8Pkcs12 =
        "MIIEDwIBAzCCA8sGCSqGSIb3DQEHAaCCA7wEggO4MIIDtDCCAW0GCSqGSIb3DQEHAaCCAV4EggFaMIIBVjCCAVIGCyqGSIb3DQEM" +
        "CgECoIHMMIHJMBwGCiqGSIb3DQEMAQMwDgQIHVPkyMUSA8MCAgfQBIGoPigMoLJSbcuiq0urgnPXssF8vh5k7mpaUBOQERpWGT0S" +
        "6hfMhnoo5G98OC4jcTMHzwfT4pcJItJZKAK/a4QsjpyI0UgIBpbDyWnN2u0WSv7aJjZ/vXvzjYzA+ZO8xiB5mvEZkoRmbGauN3wV" +
        "epg56ND1jJB9HhamOPJsBdn2rTtgGMCC6tRyZAEELUXdFDUGnb1gGgMSzPXjIpugr732FwvFkloQDDIUMXQwEwYJKoZIhvcNAQkV" +
        "MQYEBAEAAAAwXQYJKwYBBAGCNxEBMVAeTgBNAGkAYwByAG8AcwBvAGYAdAAgAFMAbwBmAHQAdwBhAHIAZQAgAEsAZQB5ACAAUwB0" +
        "AG8AcgBhAGcAZQAgAFAAcgBvAHYAaQBkAGUAcjCCAj8GCSqGSIb3DQEHBqCCAjAwggIsAgEAMIICJQYJKoZIhvcNAQcBMBwGCiqG" +
        "SIb3DQEMAQMwDgQIFGy+4pVbFyMCAgfQgIIB+CRyymJpgZRh8VJOGyQy+ieAZjDeT8y+FKnXXwfKByLG6zzpXtQI37vtbb5nxM+z" +
        "p61FDAShqMHVNSEflIJ66xgUDExY6o0ojGPsrCH9eclaf63T9WWYJH7n21M9r5S9SMKczxylXcnIGk1tccehwxur3zJLuIg0ni1y" +
        "FZrnHYnpFTexulFByYdhm2xwspuzCsrAZ82Wgk905143KLdMYCyUyaSay38VKCWvGS0vvSm593juIuwbPuunnXGZHbpoDFMQmoaL" +
        "YqLmxUy5+CPAJEzQ71U7i1k1PxOwdo4WkLNB+m3BXCJacnXKSMNp/pWyUcAbFwv95bn/ZvBTndQWxsuLUUmVEWU1p2D+4dqB8Lg8" +
        "A3xk4wiOmlsyawGbFIIqrI8Dlw320nJpkgiG0nZvlqAOetwLhQg93lkgG/hKth9ZAc36ngt6PhfaLVpIGm9ro4ZTLNhbcAhrPhk1" +
        "RwY7fzbBNaei9bjgMJNijoDiX4t6Mh60nxvJPNaWF4WjSOx8n8t9mcTiewiEyPixUkYxcG9EwI4PnFiNLVX9SpKvzGfa4efKJloO" +
        "G7D0py2/wGmH++YABbfd2ZVP2PeUrPjrddd3qkL1iuRnGSKOvOOFxVmvX3KsgK5CUlAuVjJUJxf8vVzcEdas8aIa28aaaC0Gz3Ok" +
        "7aQhLz/1HDA7MB8wBwYFKw4DAhoEFNW9brzVkAJM6SrWMnGn1mFnVsD5BBR1xEfCJqCEwrJDiNSfZ1aaBkmlYAICB9A=";

    private const string Net8Thumbprint = "F58D1B6F2E88C57BC98C8EB1F991F3F46F80C8AD";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void CertificateWrittenByNet8Host_LoadsWithItsKey_AndIsNotReplaced()
    {
        var file = WriteNet8CertificateFile();
        var before = File.ReadAllBytes(file);

        using var certificate = new HostCertificateStore(_directory, "FIXTURE-HOST").GetOrCreate();

        Assert.Equal(Net8Thumbprint, certificate.Thumbprint);
        Assert.Equal("CN=HyperHarbor Host FIXTURE-HOST", certificate.Subject);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task CertificateWrittenByNet8Host_IsServedOverTls()
    {
        WriteNet8CertificateFile();
        using var certificate = new HostCertificateStore(_directory, "FIXTURE-HOST").GetOrCreate();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(accepted.GetStream());
            // SChannel, as Kestrel uses it: the key must be usable from the store's key container.
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = certificate },
                timeout.Token);
        });

        // The check the self-test and the update helper run: the served certificate, pinned by hash.
        var protocol = await HostTlsCheck.HandshakeAsync(IPAddress.Loopback, port, certificate, timeout.Token);
        await server;

        Assert.NotEqual(System.Security.Authentication.SslProtocols.None, protocol);
    }

    /// <summary>Writes the fixture where and how a host keeps it: DPAPI (machine scope) and a restricted ACL.</summary>
    private string WriteNet8CertificateFile()
    {
        var pfx = Convert.FromBase64String(Net8Pkcs12);
        var file = Path.Combine(_directory, "host-certificate.pfx.protected");
        ProtectedFile.WriteAllBytes(
            file,
            ProtectedData.Protect(pfx, "HyperHarbor host certificate v1"u8.ToArray(), DataProtectionScope.LocalMachine));
        return file;
    }
}
