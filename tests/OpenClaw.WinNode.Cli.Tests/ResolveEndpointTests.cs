using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class ResolveEndpointTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => dict.TryGetValue(key, out var v) ? v : null;
    }

    [Fact]
    public void Default_port_when_nothing_set()
    {
        var endpoint = CliRunner.ResolveEndpoint(new WinNodeOptions(), Env());
        Assert.Equal("http://127.0.0.1:8765/", endpoint);
    }

    [Fact]
    public void Honors_OPENCLAW_MCP_PORT_env()
    {
        var endpoint = CliRunner.ResolveEndpoint(
            new WinNodeOptions(),
            Env(("OPENCLAW_MCP_PORT", "9100")));
        Assert.Equal("http://127.0.0.1:9100/", endpoint);
    }

    [Fact]
    public void Mcp_port_flag_wins_over_env()
    {
        var endpoint = CliRunner.ResolveEndpoint(
            new WinNodeOptions { McpPortOverride = 9200 },
            Env(("OPENCLAW_MCP_PORT", "9100")));
        Assert.Equal("http://127.0.0.1:9200/", endpoint);
    }

    [Fact]
    public void Mcp_url_flag_wins_over_everything()
    {
        var endpoint = CliRunner.ResolveEndpoint(
            new WinNodeOptions
            {
                McpUrlOverride = "http://example.test:1234/mcp",
                McpPortOverride = 9999,
            },
            Env(("OPENCLAW_MCP_PORT", "9100")));
        Assert.Equal("http://example.test:1234/mcp", endpoint);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("")]
    public void Invalid_env_falls_back_to_default(string envValue)
    {
        var endpoint = CliRunner.ResolveEndpoint(
            new WinNodeOptions(),
            Env(("OPENCLAW_MCP_PORT", envValue)));
        Assert.Equal("http://127.0.0.1:8765/", endpoint);
    }

    [Fact]
    public void Whitespace_url_override_falls_through_to_port_resolution()
    {
        var endpoint = CliRunner.ResolveEndpoint(
            new WinNodeOptions { McpUrlOverride = "   " },
            Env());
        Assert.Equal("http://127.0.0.1:8765/", endpoint);
    }
}
