using System.Net;
using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

/// <summary>
/// Tests covering bearer-token resolution: --mcp-token > $OPENCLAW_MCP_TOKEN >
/// on-disk mcp-token.txt under $OPENCLAW_TRAY_DATA_DIR (or %APPDATA%\OpenClawTray).
/// The on-disk path is sandboxed through a temp directory so these tests stay
/// hermetic on a developer machine that already has a real tray installed.
/// </summary>
public class AuthTokenTests : IDisposable
{
    private readonly string _sandboxDir;

    public AuthTokenTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"winnode-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { /* best effort */ }
    }

    private Func<string, string?> SandboxEnv(string? mcpToken = null) => key => key switch
    {
        "OPENCLAW_TRAY_DATA_DIR" => _sandboxDir,
        "OPENCLAW_MCP_TOKEN" => mcpToken,
        _ => null,
    };

    private static (StringWriter Out, StringWriter Err) Buffers()
        => (new StringWriter(), new StringWriter());

    [Fact]
    public async Task No_token_anywhere_sends_no_authorization_header()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Null(server.LastRequestAuthorization);
    }

    [Fact]
    public async Task McpToken_flag_sets_bearer_header_with_literal_value()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--mcp-token", "flag-token-123" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Equal("Bearer flag-token-123", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task OPENCLAW_MCP_TOKEN_env_var_sets_bearer_header()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv(mcpToken: "env-token-456"));

        Assert.Equal(0, exit);
        Assert.Equal("Bearer env-token-456", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task McpToken_flag_takes_precedence_over_env_var()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--mcp-token", "flag-wins" },
            o, e, SandboxEnv(mcpToken: "env-loses"));

        Assert.Equal(0, exit);
        Assert.Equal("Bearer flag-wins", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Token_file_under_OPENCLAW_TRAY_DATA_DIR_is_loaded_automatically()
    {
        // Mirrors the live flow: the tray writes mcp-token.txt to the sandbox
        // dir, the CLI launched with the same OPENCLAW_TRAY_DATA_DIR finds it.
        var tokenFromFile = "file-token-789";
        File.WriteAllText(Path.Combine(_sandboxDir, "mcp-token.txt"), tokenFromFile);

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Equal($"Bearer {tokenFromFile}", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Env_var_takes_precedence_over_token_file()
    {
        File.WriteAllText(Path.Combine(_sandboxDir, "mcp-token.txt"), "file-loses");

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv(mcpToken: "env-wins"));

        Assert.Equal(0, exit);
        Assert.Equal("Bearer env-wins", server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Empty_token_file_is_treated_as_missing()
    {
        File.WriteAllText(Path.Combine(_sandboxDir, "mcp-token.txt"), "   ");

        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Null(server.LastRequestAuthorization);
    }

    [Fact]
    public async Task Verbose_reports_auth_source_to_stderr()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--mcp-token", "secret", "--verbose" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.Contains("auth: bearer", stderr);
        Assert.Contains("--mcp-token", stderr);
        // Don't print the secret itself.
        Assert.DoesNotContain("secret", stderr);
    }

    [Fact]
    public async Task Verbose_reports_no_auth_when_token_missing()
    {
        using var server = new FakeMcpServer();
        var (o, e) = Buffers();

        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--verbose" },
            o, e, SandboxEnv());

        Assert.Equal(0, exit);
        Assert.Contains("auth: none", e.ToString());
    }

    [Fact]
    public void ResolveTokenPath_uses_OPENCLAW_TRAY_DATA_DIR_when_set()
    {
        Func<string, string?> env = k => k == "OPENCLAW_TRAY_DATA_DIR" ? @"C:\sandbox" : null;
        var path = CliRunner.ResolveTokenPath(env);
        Assert.Equal(Path.Combine(@"C:\sandbox", "mcp-token.txt"), path);
    }

    [Fact]
    public void ResolveTokenPath_falls_back_to_AppData_OpenClawTray()
    {
        Func<string, string?> env = _ => null;
        var path = CliRunner.ResolveTokenPath(env);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray",
            "mcp-token.txt");
        Assert.Equal(expected, path);
    }
}
