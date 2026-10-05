using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Updates;

namespace HyperHarbor.Host.Tests.Updates;

/// <summary>Serves fixed responses by URL and records what was asked for.</summary>
internal sealed class FakeReleaseServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

    public List<string> Requested { get; } = [];

    public void Serve(string url, byte[] body, bool omitLength = false) => _routes[url] = () =>
    {
        // A non-seekable stream has no Content-Length, like a chunked response.
        HttpContent content = omitLength ? new StreamContent(new UnknownLengthStream(body)) : new ByteArrayContent(body);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    };

    public void Redirect(string from, string to) => _routes[from] = () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to);
        return response;
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requested.Add(url);
        return Task.FromResult(_routes.TryGetValue(url, out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class UnknownLengthStream(byte[] body) : MemoryStream(body)
{
    public override bool CanSeek => false;
}

internal static class Releases
{
    public const string ManifestUrl = "https://github.com/faulkner-engineering/hyperharbor/releases/latest/download/latest.json";
    public const string PackageUrl = "https://github.com/faulkner-engineering/hyperharbor/releases/download/v1.2.0/HyperHarbor-Host-1.2.0.exe";
    public const string AssetUrl = "https://release-assets.githubusercontent.com/github-production-release-asset/1/HyperHarbor-Host-1.2.0.exe";

    public static readonly byte[] Package = Encoding.UTF8.GetBytes("the HyperHarbor 1.2.0 executable");

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Manifest(
        string version = "1.2.0",
        string channel = "stable",
        string url = PackageUrl,
        long? size = null,
        string? sha256 = null,
        string? minimumUpdateFrom = null,
        int schemaVersion = 1) =>
        $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "channel": "{{channel}}",
          "version": "{{version}}",
          "publishedAt": "2026-11-01T12:00:00Z",
          {{(minimumUpdateFrom is null ? string.Empty : $"\"minimumUpdateFrom\": \"{minimumUpdateFrom}\",")}}
          "dataFormat": 1,
          "notesUrl": "https://github.com/faulkner-engineering/hyperharbor/releases/tag/v{{version}}",
          "packages": [
            { "component": "host", "arch": "x64", "url": "{{url}}", "size": {{size ?? Package.Length}}, "sha256": "{{sha256 ?? Sha256(Package)}}" }
          ],
          "verification": {},
          "futureField": "ignored"
        }
        """;

    public static UpdateDownloader Downloader(FakeReleaseServer server, UpdateOptions? options = null) =>
        new(new HttpClient(server), options ?? new UpdateOptions());
}

/// <summary>A self-test of the new version (Releases.Version) that passes.</summary>
internal sealed class PassingSelfTest : ISelfTestProcess
{
    public Task<(int ExitCode, string StandardError)?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = new SelfTestResult("1.2.0", true, [new SelfTestCheck("stores", true, "ok")]);
        File.WriteAllBytes(arguments[2], JsonSerializer.SerializeToUtf8Bytes(result, SelfTestResult.JsonOptions));
        return Task.FromResult<(int, string)?>((0, string.Empty));
    }
}
