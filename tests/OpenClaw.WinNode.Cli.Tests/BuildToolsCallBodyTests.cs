using System.Text.Json;
using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class BuildToolsCallBodyTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Produces_jsonrpc_envelope_with_tools_call_method()
    {
        var body = CliRunner.BuildToolsCallBody("system.which", Args("{\"bins\":[\"git\"]}"));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("tools/call", root.GetProperty("method").GetString());

        var p = root.GetProperty("params");
        Assert.Equal("system.which", p.GetProperty("name").GetString());
        var args = p.GetProperty("arguments");
        Assert.Equal(JsonValueKind.Array, args.GetProperty("bins").ValueKind);
        Assert.Equal("git", args.GetProperty("bins")[0].GetString());
    }

    [Fact]
    public void Empty_object_args_round_trip()
    {
        var body = CliRunner.BuildToolsCallBody("screen.list", Args("{}"));
        using var doc = JsonDocument.Parse(body);
        var args = doc.RootElement.GetProperty("params").GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.Empty(args.EnumerateObject());
    }

    [Fact]
    public void Nested_args_preserve_structure()
    {
        var json = "{\"a\":{\"b\":[1,2,{\"c\":\"d\"}]},\"e\":true,\"f\":null}";
        var body = CliRunner.BuildToolsCallBody("x.y", Args(json));
        using var doc = JsonDocument.Parse(body);
        var args = doc.RootElement.GetProperty("params").GetProperty("arguments");
        Assert.Equal("d", args.GetProperty("a").GetProperty("b")[2].GetProperty("c").GetString());
        Assert.True(args.GetProperty("e").GetBoolean());
        Assert.Equal(JsonValueKind.Null, args.GetProperty("f").ValueKind);
    }
}
