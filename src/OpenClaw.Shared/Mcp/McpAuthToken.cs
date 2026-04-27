using System;
using System.IO;
using System.Security.Cryptography;

namespace OpenClaw.Shared.Mcp;

/// <summary>
/// Manages the MCP server's bearer token.
///
/// The token lives in <c>%LOCALAPPDATA%\OpenClaw\mcp-token.txt</c>. Local CLIs
/// (the planned `openclaw` CLI) and per-user agent registrations read it from
/// that path so registration is a single mechanical step rather than a config
/// mutation. The file inherits the LocalAppData ACL — by default only the
/// current user (and SYSTEM/Administrators) can read it. Plus the loopback
/// bind keeps the server invisible to other machines, and the Origin/Host
/// checks block browser cross-origin attacks.
/// </summary>
public static class McpAuthToken
{
    private const string FileName = "mcp-token.txt";

    /// <summary>Default path under the user's LocalAppData. Created on first read.</summary>
    public static string DefaultPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "OpenClaw", FileName);
        }
    }

    /// <summary>
    /// Load the token from <see cref="DefaultPath"/>, creating a fresh random
    /// one if the file does not exist. Returns the token string.
    /// </summary>
    public static string LoadOrCreate() => LoadOrCreate(DefaultPath);

    public static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing)) return existing;
            }
            catch { /* fall through and regenerate */ }
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var token = Generate();
        File.WriteAllText(path, token);
        return token;
    }

    /// <summary>Read the token without creating a new one. Returns null when missing.</summary>
    public static string? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch { return null; }
    }

    private static string Generate()
    {
        // 32 bytes ≈ 256 bits, base64url so it's URL/header safe.
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
