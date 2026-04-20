using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for ExecEnvSanitizer — env-variable injection prevention.
/// </summary>
public class ExecEnvSanitizerTests
{
    // --- IsBlocked ---

    [Theory]
    [InlineData("PATH")]
    [InlineData("path")]
    [InlineData("Path")]
    [InlineData("PATHEXT")]
    [InlineData("ComSpec")]
    [InlineData("COMSPEC")]
    [InlineData("PSModulePath")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("node_options")]
    [InlineData("NODE_PATH")]
    [InlineData("PYTHONPATH")]
    [InlineData("PYTHONSTARTUP")]
    [InlineData("PYTHONUSERBASE")]
    [InlineData("RUBYOPT")]
    [InlineData("RUBYLIB")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    [InlineData("ZDOTDIR")]
    [InlineData("GIT_SSH")]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("GIT_EXEC_PATH")]
    [InlineData("GIT_PROXY_COMMAND")]
    [InlineData("GIT_ASKPASS")]
    [InlineData("LD_PRELOAD")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("LD_AUDIT")]
    [InlineData("LD_ANYTHING_ELSE")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("DYLD_LIBRARY_PATH")]
    [InlineData("DYLD_CUSTOM")]
    [InlineData("PERL5OPT")]
    [InlineData("PERL5LIB")]
    [InlineData("PERLIO")]
    public void IsBlocked_ReturnsTrueForDangerousVars(string name)
    {
        Assert.True(ExecEnvSanitizer.IsBlocked(name));
    }

    [Theory]
    [InlineData("MY_VAR")]
    [InlineData("APP_CONFIG")]
    [InlineData("LOG_LEVEL")]
    [InlineData("DEBUG")]
    [InlineData("CUSTOM_PATH_SUFFIX")]  // contains PATH but doesn't match denylist
    [InlineData("MY_ENV")]             // contains ENV but doesn't match exactly
    [InlineData("RAILS_ENV")]          // ends with _ENV but prefix is not LD_/DYLD_
    [InlineData("TZ")]
    [InlineData("LANG")]
    [InlineData("LC_ALL")]
    [InlineData("HOME")]
    public void IsBlocked_ReturnsFalseForSafeVars(string name)
    {
        Assert.False(ExecEnvSanitizer.IsBlocked(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("KEY=VALUE")]   // '=' in name is invalid
    public void IsBlocked_ReturnsTrueForInvalidNames(string name)
    {
        Assert.True(ExecEnvSanitizer.IsBlocked(name));
    }

    // --- Sanitize ---

    [Fact]
    public void Sanitize_AllowsSafeVars()
    {
        var input = new Dictionary<string, string>
        {
            ["LOG_LEVEL"] = "debug",
            ["APP_ENV"] = "production",
            ["TZ"] = "UTC",
        };

        var result = ExecEnvSanitizer.Sanitize(input);

        Assert.Equal(3, result.Allowed.Count);
        Assert.Empty(result.Blocked);
        Assert.Equal("debug", result.Allowed["LOG_LEVEL"]);
        Assert.Equal("production", result.Allowed["APP_ENV"]);
        Assert.Equal("UTC", result.Allowed["TZ"]);
    }

    [Fact]
    public void Sanitize_BlocksDangerousVars()
    {
        var input = new Dictionary<string, string>
        {
            ["NODE_OPTIONS"] = "--require=/tmp/evil.js",
            ["LD_PRELOAD"] = "/evil.so",
            ["GIT_SSH_COMMAND"] = "evil.sh",
        };

        var result = ExecEnvSanitizer.Sanitize(input);

        Assert.Empty(result.Allowed);
        Assert.Equal(3, result.Blocked.Count);
        Assert.Contains("NODE_OPTIONS", result.Blocked);
        Assert.Contains("LD_PRELOAD", result.Blocked);
        Assert.Contains("GIT_SSH_COMMAND", result.Blocked);
    }

    [Fact]
    public void Sanitize_MixedInput_SeparatesCorrectly()
    {
        var input = new Dictionary<string, string>
        {
            ["LOG_LEVEL"] = "info",
            ["PATH"] = "/evil/bin",
            ["APP_SECRET"] = "mysecret",
            ["DYLD_INSERT_LIBRARIES"] = "/hack.dylib",
        };

        var result = ExecEnvSanitizer.Sanitize(input);

        Assert.Equal(2, result.Allowed.Count);
        Assert.Equal(2, result.Blocked.Count);
        Assert.True(result.Allowed.ContainsKey("LOG_LEVEL"));
        Assert.True(result.Allowed.ContainsKey("APP_SECRET"));
        Assert.Contains("PATH", result.Blocked);
        Assert.Contains("DYLD_INSERT_LIBRARIES", result.Blocked);
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmptyResult()
    {
        var result = ExecEnvSanitizer.Sanitize(new Dictionary<string, string>());

        Assert.Empty(result.Allowed);
        Assert.Empty(result.Blocked);
    }

    [Fact]
    public void Sanitize_LdPrefixVariants_AllBlocked()
    {
        var input = new Dictionary<string, string>
        {
            ["LD_PRELOAD"] = "x",
            ["LD_LIBRARY_PATH"] = "x",
            ["LD_AUDIT"] = "x",
            ["ld_preload"] = "x",           // lowercase should also be blocked
        };

        var result = ExecEnvSanitizer.Sanitize(input);

        Assert.Empty(result.Allowed);
        Assert.Equal(4, result.Blocked.Count);
    }

    // --- Integration with SystemCapability ---

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async System.Threading.Tasks.Task SystemRun_BlocksDangerousEnvVars_AndProceedsWithSafe()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r1",
            Command = "system.run",
            Args = Parse("""
                {
                    "command": "echo hello",
                    "env": {
                        "LOG_LEVEL": "debug",
                        "NODE_OPTIONS": "--require=/evil.js",
                        "LD_PRELOAD": "/evil.so"
                    }
                }
                """)
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        // Only safe vars should reach the runner
        Assert.NotNull(runner.LastRequest);
        Assert.NotNull(runner.LastRequest!.Env);
        Assert.True(runner.LastRequest.Env!.ContainsKey("LOG_LEVEL"));
        Assert.False(runner.LastRequest.Env.ContainsKey("NODE_OPTIONS"));
        Assert.False(runner.LastRequest.Env.ContainsKey("LD_PRELOAD"));
    }

    [Fact]
    public async System.Threading.Tasks.Task SystemRun_AllDangerous_EnvIsNull()
    {
        var runner = new FakeCommandRunner();
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r2",
            Command = "system.run",
            Args = Parse("""{"command":"echo hi","env":{"PATH":"/evil","NODE_OPTIONS":"--require=x"}}""")
        };

        var res = await cap.ExecuteAsync(req);

        Assert.True(res.Ok);
        Assert.NotNull(runner.LastRequest);
        // Env should be null (no safe vars) rather than an empty dict
        Assert.Null(runner.LastRequest!.Env);
    }
}

/// <summary>FakeCommandRunner that captures the last request for assertion.</summary>
file sealed class FakeCommandRunner : ICommandRunner
{
    public string Name => "fake";
    public CommandRequest? LastRequest { get; private set; }

    public System.Threading.Tasks.Task<CommandResult> RunAsync(CommandRequest request, System.Threading.CancellationToken ct = default)
    {
        LastRequest = request;
        return System.Threading.Tasks.Task.FromResult(new CommandResult
        {
            Stdout = "ok",
            ExitCode = 0
        });
    }
}
