using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class RunAsyncTests
{
    // Tests run on developer machines where %APPDATA%\OpenClawTray\mcp-token.txt
    // may exist with the live tray's token. Without an override, the CLI's
    // automatic loader would happily pick that up and set an Authorization
    // header for every test request, which is hermeticity-poison even if the
    // FakeMcpServer ignores it. Redirect via OPENCLAW_TRAY_DATA_DIR (same
    // sandbox env var the tray and integration tests honor) at a guaranteed-
    // empty temp directory so the loader finds no file and runs without auth.
    private static readonly string SandboxDataDir =
        Path.Combine(Path.GetTempPath(), $"winnode-test-sandbox-{Guid.NewGuid():N}");

    private static Func<string, string?> EmptyEnv => key =>
        key == "OPENCLAW_TRAY_DATA_DIR" ? SandboxDataDir : null;

    private static (StringWriter Out, StringWriter Err) Buffers()
        => (new StringWriter(), new StringWriter());

    [Fact]
    public async Task No_args_prints_usage_and_exits_2()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(Array.Empty<string>(), o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("winnode", o.ToString());
        Assert.Equal("", e.ToString());
    }

    [Fact]
    public async Task Help_flag_prints_usage_and_exits_0()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "--help" }, o, e, EmptyEnv);
        Assert.Equal(0, exit);
        Assert.Contains("Usage:", o.ToString());
        Assert.Equal("", e.ToString());
    }

    [Fact]
    public async Task Short_help_flag_works()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "-h" }, o, e, EmptyEnv);
        Assert.Equal(0, exit);
        Assert.Contains("--command", o.ToString());
    }

    [Fact]
    public async Task Argument_error_prints_message_and_usage()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "--bogus", "x" }, o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("--bogus", e.ToString());
        Assert.Contains("Usage:", o.ToString());
    }

    [Fact]
    public async Task Missing_command_exits_2()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(new[] { "--node", "x" }, o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("--command is required", e.ToString());
    }

    [Fact]
    public async Task Params_must_be_valid_json()
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--params", "not json" },
            o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("not valid JSON", e.ToString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Params_must_be_object(string nonObject)
    {
        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--params", nonObject },
            o, e, EmptyEnv);
        Assert.Equal(2, exit);
        Assert.Contains("must be a JSON object", e.ToString());
    }

    [Fact]
    public async Task Connection_refused_exits_1_with_hint()
    {
        // Pick a port that's almost certainly closed.
        var port = FindClosedPort();

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-port", port.ToString() },
            o, e, EmptyEnv);
        Assert.Equal(1, exit);
        var stderr = e.ToString();
        Assert.Contains("failed to reach MCP server", stderr);
        Assert.Contains("Local MCP Server", stderr);
    }

    [Fact]
    public async Task Successful_call_pretty_prints_payload_and_sends_correct_envelope()
    {
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.OK,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"sent\\\":true}\"}],\"isError\":false}}",
                "application/json"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[]
            {
                "--command", "system.notify",
                "--params", "{\"body\":\"hi\"}",
                "--mcp-url", server.Url,
            },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        Assert.Contains("\"sent\": true", o.ToString());
        Assert.Equal("", e.ToString());

        // Verify the wire format the server actually saw.
        Assert.Equal("POST", server.LastRequestMethod);
        Assert.StartsWith("application/json", server.LastRequestContentType ?? "");
        using var sent = JsonDocument.Parse(server.LastRequestBody!);
        Assert.Equal("2.0", sent.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("tools/call", sent.RootElement.GetProperty("method").GetString());
        var p = sent.RootElement.GetProperty("params");
        Assert.Equal("system.notify", p.GetProperty("name").GetString());
        Assert.Equal("hi", p.GetProperty("arguments").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Tool_error_response_writes_to_stderr_and_exits_1()
    {
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.OK,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"camera offline\"}],\"isError\":true}}",
                "application/json"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "camera.snap", "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        Assert.Contains("camera offline", e.ToString());
    }

    [Fact]
    public async Task Http_500_writes_status_and_body_to_stderr_and_exits_1()
    {
        using var server = new FakeMcpServer
        {
            Responder = _ => (HttpStatusCode.InternalServerError, "kaboom", "text/plain"),
        };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "x", "--mcp-url", server.Url },
            o, e, EmptyEnv);

        Assert.Equal(1, exit);
        var stderr = e.ToString();
        Assert.Contains("MCP HTTP 500", stderr);
        Assert.Contains("kaboom", stderr);
    }

    [Fact]
    public async Task Timeout_writes_message_and_exits_1()
    {
        using var server = new FakeMcpServer { HoldForever = true };

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[]
            {
                "--command", "x",
                "--mcp-url", server.Url,
                // CliRunner adds 5000ms buffer to the HTTP timeout, so keep this
                // small so the test stays under a second.
                "--invoke-timeout", "1",
            },
            o, e, EmptyEnv);

        // The HttpClient timeout fires (1 + 5000 ms buffer = ~5s); test budget OK.
        // Wider window for slow CI: the 5s ceiling matters only as an upper bound,
        // not for correctness.
        Assert.Equal(1, exit);
        Assert.Contains("timed out", e.ToString());
    }

    [Fact]
    public async Task Verbose_logs_endpoint_and_ignored_flags_to_stderr()
    {
        using var server = new FakeMcpServer();

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[]
            {
                "--node", "winbox-1",
                "--idempotency-key", "abc",
                "--command", "screen.list",
                "--mcp-url", server.Url,
                "--verbose",
            },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.Contains(server.Url, stderr);
        Assert.Contains("screen.list", stderr);
        Assert.Contains("--node \"winbox-1\" ignored", stderr);
        Assert.Contains("--idempotency-key ignored", stderr);
    }

    [Fact]
    public async Task Verbose_without_node_or_key_omits_their_lines()
    {
        using var server = new FakeMcpServer();

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list", "--mcp-url", server.Url, "--verbose" },
            o, e, EmptyEnv);

        Assert.Equal(0, exit);
        var stderr = e.ToString();
        Assert.DoesNotContain("--node", stderr);
        Assert.DoesNotContain("--idempotency-key", stderr);
    }

    [Fact]
    public async Task Endpoint_resolves_from_OPENCLAW_MCP_PORT_when_no_overrides()
    {
        using var server = new FakeMcpServer();
        var env = (string key) => key == "OPENCLAW_MCP_PORT" ? server.Port.ToString() : null;

        var (o, e) = Buffers();
        var exit = await CliRunner.RunAsync(
            new[] { "--command", "screen.list" },
            o, e, env);

        Assert.Equal(0, exit);
        // The server received the request → env-based port resolution worked.
        Assert.NotNull(server.LastRequestBody);
    }

    private static int FindClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
