using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class ParseArgsTests
{
    [Fact]
    public void Parses_all_flags()
    {
        var opts = CliRunner.ParseArgs(new[]
        {
            "--node", "winbox-1",
            "--command", "system.which",
            "--params", "{\"bins\":[\"git\"]}",
            "--invoke-timeout", "9000",
            "--idempotency-key", "abc-123",
            "--mcp-url", "http://127.0.0.1:9000/",
            "--mcp-port", "9001",
            "--verbose",
        });

        Assert.Equal("winbox-1", opts.Node);
        Assert.Equal("system.which", opts.Command);
        Assert.Equal("{\"bins\":[\"git\"]}", opts.Params);
        Assert.Equal(9000, opts.InvokeTimeoutMs);
        Assert.Equal("abc-123", opts.IdempotencyKey);
        Assert.Equal("http://127.0.0.1:9000/", opts.McpUrlOverride);
        Assert.Equal(9001, opts.McpPortOverride);
        Assert.True(opts.Verbose);
    }

    [Fact]
    public void Defaults_when_only_command_given()
    {
        var opts = CliRunner.ParseArgs(new[] { "--command", "screen.list" });
        Assert.Equal("screen.list", opts.Command);
        Assert.Equal("{}", opts.Params);
        Assert.Equal(15000, opts.InvokeTimeoutMs);
        Assert.Null(opts.Node);
        Assert.Null(opts.IdempotencyKey);
        Assert.Null(opts.McpUrlOverride);
        Assert.Null(opts.McpPortOverride);
        Assert.False(opts.Verbose);
    }

    [Theory]
    [InlineData("--node")]
    [InlineData("--command")]
    [InlineData("--params")]
    [InlineData("--invoke-timeout")]
    [InlineData("--idempotency-key")]
    [InlineData("--mcp-url")]
    [InlineData("--mcp-port")]
    public void Missing_value_for_flag_throws(string flag)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliRunner.ParseArgs(new[] { flag }));
        Assert.Contains(flag, ex.Message);
    }

    [Fact]
    public void Unknown_flag_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => CliRunner.ParseArgs(new[] { "--bogus", "x" }));
        Assert.Contains("--bogus", ex.Message);
    }

    [Theory]
    [InlineData("--invoke-timeout", "abc")]
    [InlineData("--invoke-timeout", "0")]
    [InlineData("--invoke-timeout", "-5")]
    [InlineData("--mcp-port", "not-a-number")]
    [InlineData("--mcp-port", "0")]
    public void Invalid_int_throws(string flag, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliRunner.ParseArgs(new[] { flag, value }));
        Assert.Contains(flag, ex.Message);
    }
}
