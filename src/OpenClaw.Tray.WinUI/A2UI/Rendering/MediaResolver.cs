using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
        // Rough size cap based on encoded length; base64 → 4/3 expansion.
        var encoded = url.Length - comma - 1;
        var approx = (long)(encoded * 0.75);
        return approx <= DataUrlMaxBytes;
    }

    public async Task<BitmapImage?> LoadImageAsync(string url)
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
                return await BitmapFromBytes(bytes);
            }
            var data = await s_http.GetByteArrayAsync(url);
            return await BitmapFromBytes(data);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[A2UI] Image fetch failed for {Truncate(url)}: {ex.Message}");
            return null;
        }
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
