using System.Net;
using System.Security.Cryptography;

namespace HyperHarbor.Host.Core.Updates;

/// <summary>
/// Fetches manifests and packages over https from allowed hosts only. Redirects are followed by hand so each
/// hop is checked against <see cref="UpdateOptions.AllowedHosts"/>. A package is written to a ".partial" file,
/// hashed as it arrives, held to the manifest's size, and moved into place only when its SHA-256 matches.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, UpdateOptions options)
{
    private const int MaxRedirects = 5;
    private const int BufferBytes = 81920;

    /// <summary>An HttpClient that leaves redirects to this class.</summary>
    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <exception cref="UpdateRejectedException">A rule was broken, or the manifest is larger than <see cref="UpdateManifest.MaxBytes"/>.</exception>
    public async Task<byte[]> GetManifestAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[BufferBytes];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > UpdateManifest.MaxBytes)
            {
                throw new UpdateRejectedException($"The update manifest is larger than {UpdateManifest.MaxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Downloads <paramref name="package"/> to <paramref name="destination"/> and checks its size and SHA-256.</summary>
    /// <exception cref="UpdateRejectedException">The download broke a rule or did not match; nothing is left behind.</exception>
    public async Task DownloadAsync(UpdatePackage package, string destination, CancellationToken cancellationToken)
    {
        var partial = destination + ".partial";
        try
        {
            using (var response = await SendAsync(UpdateManifest.CheckUrl(package.Url, options), cancellationToken).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is { } length && length != package.Size)
                {
                    throw new UpdateRejectedException($"The package is {length} bytes; the manifest says {package.Size}.");
                }

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var chunk = new byte[BufferBytes];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > package.Size)
                    {
                        throw new UpdateRejectedException($"The package is larger than the {package.Size} bytes the manifest says.");
                    }

                    hash.AppendData(chunk, 0, read);
                    await file.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total != package.Size)
                {
                    throw new UpdateRejectedException($"The package is {total} bytes; the manifest says {package.Size}.");
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateRejectedException($"The package's SHA-256 is {actual.ToLowerInvariant()}; the manifest says {package.Sha256.ToLowerInvariant()}.");
                }
            }

            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            UpdateManifest.CheckUrl(url.AbsoluteUri, options);
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new UpdateRejectedException($"{url.IdnHost} redirected without a location.");
                response.Dispose();
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"{url.GetLeftPart(UriPartial.Path)} returned {status}.", null, (HttpStatusCode)status);
            }

            return response;
        }

        throw new UpdateRejectedException($"More than {MaxRedirects} redirects.");
    }
}
