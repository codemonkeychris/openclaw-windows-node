using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using Windows.Storage.Streams;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Single resolver for image/video/audio URLs in A2UI surfaces. Enforces the
/// security policy from the spec:
///   - https://&lt;allowlist&gt; only
///   - data: image/png|jpeg|webp up to 2 MiB
///   - everything else → broken-image fallback (logged once)
/// Allowlist is mutable at runtime so a future <c>createSurface.theme</c> /
/// manifest can extend it without restarting the process.
/// </summary>
public sealed class MediaResolver
{
    private readonly IOpenClawLogger _logger;
    private readonly HashSet<string> _hostAllowlist = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient s_http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private const long DataUrlMaxBytes = 2L * 1024 * 1024;
    /// <summary>Hard cap on remote image bytes. Sized to dwarf realistic UI imagery while still preventing OOM from a hostile/compromised allowlisted host.</summary>
    internal const long RemoteImageMaxBytes = 8L * 1024 * 1024;

    public MediaResolver(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public void AllowHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        _hostAllowlist.Add(host);
    }

    public bool IsAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return IsSafeDataImage(url);
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return _hostAllowlist.Contains(uri.Host);
    }

    private static bool IsSafeDataImage(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return false;
        var header = url.Substring(5, comma - 5).ToLowerInvariant();
        if (!(header.StartsWith("image/png") || header.StartsWith("image/jpeg") || header.StartsWith("image/webp")))
            return false;
        // Tight size check based on actual base64 decode length (account for padding).
        var encoded = url.Length - comma - 1;
        long decoded = ComputeBase64DecodedLength(url, comma + 1, encoded);
        return decoded <= DataUrlMaxBytes;
    }

    private static long ComputeBase64DecodedLength(string url, int start, int encodedLen)
    {
        if (encodedLen <= 0) return 0;
        // Strip trailing whitespace and base64 padding for a precise upper bound.
        int padding = 0;
        int end = start + encodedLen - 1;
        while (end >= start && (url[end] == '=' || char.IsWhiteSpace(url[end])))
        {
            if (url[end] == '=') padding++;
            end--;
        }
        long realChars = end - start + 1 + padding;
        return realChars / 4 * 3 - padding;
    }

    public Task<BitmapImage?> LoadImageAsync(string url) => LoadImageAsync(url, CancellationToken.None);

    public async Task<BitmapImage?> LoadImageAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsAllowed(url))
        {
            _logger.Warn($"[A2UI] Image blocked: {Truncate(url)}");
            return null;
        }
        try
        {
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                var b64 = url.Substring(comma + 1);
                var bytes = Convert.FromBase64String(b64);
                if (bytes.LongLength > DataUrlMaxBytes)
                {
                    _logger.Warn($"[A2UI] data: image exceeds cap ({bytes.LongLength} > {DataUrlMaxBytes})");
                    return null;
                }
                return await BitmapFromBytes(bytes);
            }
            var data = await FetchBoundedAsync(url, cancellationToken).ConfigureAwait(false);
            if (data == null) return null;
            return await BitmapFromBytes(data);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"[A2UI] Image fetch failed for {Truncate(url)}: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> FetchBoundedAsync(string url, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead lets us reject by Content-Length before buffering the body.
        using var resp = await s_http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warn($"[A2UI] Image fetch HTTP {(int)resp.StatusCode} for {Truncate(url)}");
            return null;
        }
        var contentLength = resp.Content.Headers.ContentLength;
        if (contentLength is long cl && cl > RemoteImageMaxBytes)
        {
            _logger.Warn($"[A2UI] Image rejected by Content-Length ({cl} > {RemoteImageMaxBytes}) for {Truncate(url)}");
            return null;
        }

        // Stream body into a capped buffer. Servers may lie about Content-Length
        // or use chunked encoding without it, so enforce again on the read side.
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream(capacity: contentLength is long c ? (int)Math.Min(c, RemoteImageMaxBytes) : 64 * 1024);
        var buffer = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > RemoteImageMaxBytes)
            {
                _logger.Warn($"[A2UI] Image stream exceeded cap ({total} > {RemoteImageMaxBytes}) for {Truncate(url)}");
                return null;
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public Uri? AsUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;

    private static async Task<BitmapImage> BitmapFromBytes(byte[] bytes)
    {
        var bmp = new BitmapImage();
        using var ms = new InMemoryRandomAccessStream();
        using (var w = new DataWriter(ms))
        {
            w.WriteBytes(bytes);
            await w.StoreAsync();
            await w.FlushAsync();
            w.DetachStream();
        }
        ms.Seek(0);
        await bmp.SetSourceAsync(ms);
        return bmp;
    }

    private static string Truncate(string s) =>
        s.Length <= 60 ? s : s.Substring(0, 60) + "...";
}
