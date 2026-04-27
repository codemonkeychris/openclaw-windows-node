using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OpenClaw.WinNode.Cli;

internal sealed class WinNodeOptions
{
    public string? Node { get; set; }
    public string? Command { get; set; }
    public string Params { get; set; } = "{}";
    public int InvokeTimeoutMs { get; set; } = 15000;
    public string? IdempotencyKey { get; set; }
    public string? McpUrlOverride { get; set; }
    public int? McpPortOverride { get; set; }
    public bool Verbose { get; set; }
}

/// <summary>
/// Entry-point shim. All real work lives in <see cref="CliRunner"/> so it can
/// be exercised from unit tests without touching <see cref="Console"/> or the
/// process environment.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
        => CliRunner.RunAsync(
            args,
            Console.Out,
            Console.Error,
            Environment.GetEnvironmentVariable);
}

internal static class CliRunner
{
    internal const int DefaultMcpPort = 8765;

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> envLookup,
        HttpMessageHandler? httpHandler = null)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
        {
            PrintUsage(stdout);
            return args.Length == 0 ? 2 : 0;
        }

        WinNodeOptions options;
        try
        {
            options = ParseArgs(args);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Argument error: {ex.Message}");
            PrintUsage(stdout);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(options.Command))
        {
            stderr.WriteLine("--command is required");
            return 2;
        }

        JsonElement arguments;
        try
        {
            using var paramsDoc = JsonDocument.Parse(options.Params);
            if (paramsDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                stderr.WriteLine("--params must be a JSON object");
                return 2;
            }
            arguments = paramsDoc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"--params is not valid JSON: {ex.Message}");
            return 2;
        }

        var endpoint = ResolveEndpoint(options, envLookup);
        if (options.Verbose)
        {
            stderr.WriteLine($"[winnode] endpoint: {endpoint}");
            stderr.WriteLine($"[winnode] command: {options.Command}");
            if (!string.IsNullOrEmpty(options.Node))
            {
                stderr.WriteLine($"[winnode] --node \"{options.Node}\" ignored (always local tray)");
            }
            if (!string.IsNullOrEmpty(options.IdempotencyKey))
            {
                stderr.WriteLine("[winnode] --idempotency-key ignored (no idempotency over local MCP)");
            }
        }

        var requestBody = BuildToolsCallBody(options.Command!, arguments);

        // Add a small buffer to the HTTP timeout so the server-side timeout (if
        // any) surfaces as a tool error rather than a transport timeout.
        var httpTimeout = TimeSpan.FromMilliseconds(options.InvokeTimeoutMs + 5000);

        using var http = httpHandler is null
            ? new HttpClient { Timeout = httpTimeout }
            : new HttpClient(httpHandler, disposeHandler: false) { Timeout = httpTimeout };
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(endpoint, content);
        }
        catch (TaskCanceledException) when (httpTimeout > TimeSpan.Zero)
        {
            stderr.WriteLine($"timed out after {options.InvokeTimeoutMs}ms calling {endpoint}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            stderr.WriteLine($"failed to reach MCP server at {endpoint}: {ex.Message}");
            stderr.WriteLine("hint: enable \"Local MCP Server\" in tray Settings, then restart the tray app.");
            return 1;
        }

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            stderr.WriteLine($"MCP HTTP {(int)response.StatusCode}: {body}");
            return 1;
        }

        return EmitResult(body, stdout, stderr);
    }

    internal static int EmitResult(string body, TextWriter stdout, TextWriter stderr)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"MCP response was not valid JSON: {ex.Message}");
            stderr.WriteLine(body);
            return 1;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "(no message)";
                var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                stderr.WriteLine($"JSON-RPC error {code}: {msg}");
                return 1;
            }

            if (!root.TryGetProperty("result", out var result))
            {
                stderr.WriteLine("MCP response missing 'result'");
                stderr.WriteLine(body);
                return 1;
            }

            var isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
            string? text = null;
            if (result.TryGetProperty("content", out var contentArr) &&
                contentArr.ValueKind == JsonValueKind.Array &&
                contentArr.GetArrayLength() > 0)
            {
                var first = contentArr[0];
                if (first.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString();
                }
            }

            if (isError)
            {
                stderr.WriteLine(text ?? "tool execution failed");
                return 1;
            }

            // text is the capability payload re-serialized as JSON. Re-emit it
            // (pretty-printed) so the output matches what `openclaw nodes invoke`
            // produces via writeJson.
            if (text is null)
            {
                stdout.WriteLine(PrettyPrint(result));
                return 0;
            }

            try
            {
                using var inner = JsonDocument.Parse(text);
                stdout.WriteLine(PrettyPrint(inner.RootElement));
            }
            catch (JsonException)
            {
                stdout.WriteLine(text);
            }
            return 0;
        }
    }

    internal static string PrettyPrint(JsonElement element)
        => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    internal static string BuildToolsCallBody(string command, JsonElement arguments)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", 1);
            w.WriteString("method", "tools/call");
            w.WriteStartObject("params");
            w.WriteString("name", command);
            w.WritePropertyName("arguments");
            arguments.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string ResolveEndpoint(WinNodeOptions options, Func<string, string?> envLookup)
    {
        if (!string.IsNullOrWhiteSpace(options.McpUrlOverride))
        {
            return options.McpUrlOverride!;
        }

        var port = options.McpPortOverride
            ?? (int.TryParse(
                envLookup("OPENCLAW_MCP_PORT"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var envPort) && envPort > 0 ? envPort : DefaultMcpPort);

        return $"http://127.0.0.1:{port}/";
    }

    internal static WinNodeOptions ParseArgs(string[] args)
    {
        var options = new WinNodeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--node":
                    options.Node = RequireValue(args, ref i, arg);
                    break;
                case "--command":
                    options.Command = RequireValue(args, ref i, arg);
                    break;
                case "--params":
                    options.Params = RequireValue(args, ref i, arg);
                    break;
                case "--invoke-timeout":
                    options.InvokeTimeoutMs = ParseInt(RequireValue(args, ref i, arg), min: 1, name: arg);
                    break;
                case "--idempotency-key":
                    options.IdempotencyKey = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-url":
                    options.McpUrlOverride = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-port":
                    options.McpPortOverride = ParseInt(RequireValue(args, ref i, arg), min: 1, name: arg);
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {name}");
        }
        index++;
        return args[index];
    }

    private static int ParseInt(string value, int min, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < min)
        {
            throw new ArgumentException($"{name} must be an integer >= {min}");
        }
        return parsed;
    }

    internal static void PrintUsage(TextWriter stdout)
    {
        stdout.WriteLine("winnode - invoke OpenClaw node commands on the local Windows tray over MCP");
        stdout.WriteLine();
        stdout.WriteLine("Mirrors the flag surface of `openclaw nodes invoke`. The --node value is");
        stdout.WriteLine("accepted but ignored; calls always target the local tray's MCP server");
        stdout.WriteLine("(default http://127.0.0.1:8765/). Enable \"Local MCP Server\" in tray Settings.");
        stdout.WriteLine();
        stdout.WriteLine("Usage:");
        stdout.WriteLine("  winnode --command <command> [--params <json>] [options]");
        stdout.WriteLine();
        stdout.WriteLine("Options:");
        stdout.WriteLine("  --node <idOrNameOrIp>        Accepted for parity with `openclaw nodes invoke`; ignored");
        stdout.WriteLine("  --command <command>          Command to invoke (e.g. system.which, canvas.eval) [required]");
        stdout.WriteLine("  --params <json>              JSON object string for params (default: {})");
        stdout.WriteLine("  --invoke-timeout <ms>        Invoke timeout in ms (default: 15000)");
        stdout.WriteLine("  --idempotency-key <key>      Accepted for parity; ignored over local MCP");
        stdout.WriteLine("  --mcp-url <url>              Override MCP endpoint (default: http://127.0.0.1:<port>/)");
        stdout.WriteLine("  --mcp-port <port>            Override MCP port (default: $OPENCLAW_MCP_PORT or 8765)");
        stdout.WriteLine("  --verbose                    Print endpoint + ignored flags to stderr");
        stdout.WriteLine("  --help, -h                   Show this help");
        stdout.WriteLine();
        stdout.WriteLine("Examples:");
        stdout.WriteLine("  winnode --command system.which --params '{\"bins\":[\"git\",\"node\"]}'");
        stdout.WriteLine("  winnode --command screen.list");
        stdout.WriteLine("  winnode --command canvas.present --params '{\"url\":\"https://example.com\"}'");
        stdout.WriteLine();
        stdout.WriteLine("See skill.md (next to this exe) for the full agent reference: every supported");
        stdout.WriteLine("command, its argument schema, and the A2UI v0.8 JSONL grammar.");
    }
}
