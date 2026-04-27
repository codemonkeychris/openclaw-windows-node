using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Canvas capability - WebView2-based canvas for displaying content
/// </summary>
public class CanvasCapability : NodeCapabilityBase
{
    public override string Category => "canvas";
    
    private static readonly string[] _commands = new[]
    {
        "canvas.present",
        "canvas.hide",
        "canvas.navigate",
        "canvas.eval",
        "canvas.snapshot",
        "canvas.a2ui.push",
        "canvas.a2ui.pushJSONL",
        "canvas.a2ui.reset",
        "canvas.a2ui.dump",
        "canvas.caps",
    };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Events for UI to handle
    public event EventHandler<CanvasPresentArgs>? PresentRequested;
    public event EventHandler? HideRequested;
    public event EventHandler<string>? NavigateRequested;
    public event Func<string, Task<string>>? EvalRequested;
    public event Func<CanvasSnapshotArgs, Task<string>>? SnapshotRequested;
    public event EventHandler<CanvasA2UIArgs>? A2UIPushRequested;
    public event EventHandler? A2UIResetRequested;
    /// <summary>Returns a JSON state dump of the native A2UI surface graph.</summary>
    public event Func<Task<string>>? A2UIDumpRequested;
    /// <summary>Returns a JSON capability summary describing which canvas operations are supported.</summary>
    public event Func<Task<string>>? CapsRequested;
    
    public CanvasCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    private static int ClampPosition(int value)
    {
        if (value == -1) return -1; // documented "center" sentinel
        return value < MinPosition ? MinPosition : (value > MaxPosition ? MaxPosition : value);
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "canvas.present" => await HandlePresentAsync(request),
            "canvas.hide" => HandleHide(request),
            "canvas.navigate" => await HandleNavigateAsync(request),
            "canvas.eval" => await HandleEvalAsync(request),
            "canvas.snapshot" => await HandleSnapshotAsync(request),
            "canvas.a2ui.push" => HandleA2UIPush(request),
            "canvas.a2ui.pushJSONL" => HandleA2UIPush(request),
            "canvas.a2ui.reset" => HandleA2UIReset(request),
            "canvas.a2ui.dump" => await HandleA2UIDumpAsync(),
            "canvas.caps" => await HandleCapsAsync(),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    // Window-bounds clamps. -1 is the documented "center" sentinel for x/y so
    // we preserve negatives below MinPosition by routing them to -1.
    private const int MinDimension = 100;
    private const int MaxDimension = 7680;
    private const int MinPosition = -16384;
    private const int MaxPosition = 16384;
    private const int MinSnapshotWidth = 32;
    private const int MaxSnapshotWidth = 7680;
    private const int MinQuality = 1;
    private const int MaxQuality = 100;

    // A2UI push caps. Inline transport in McpHttpServer caps at 4 MiB; jsonlPath
    // bypasses that, so re-enforce here. The line-count cap protects the UI thread
    // from a single push that explodes into thousands of dispatcher posts.
    internal const long MaxA2UIJsonlBytes = 4L * 1024 * 1024;
    internal const int MaxA2UIJsonlLines = 4096;

    private Task<NodeInvokeResponse> HandlePresentAsync(NodeInvokeRequest request)
    {
        var url = GetStringArg(request.Args, "url");
        var html = GetStringArg(request.Args, "html");
        var width = Clamp(GetIntArg(request.Args, "width", 800), MinDimension, MaxDimension);
        var height = Clamp(GetIntArg(request.Args, "height", 600), MinDimension, MaxDimension);
        var x = ClampPosition(GetIntArg(request.Args, "x", -1)); // -1 = center
        var y = ClampPosition(GetIntArg(request.Args, "y", -1));
        var title = GetStringArg(request.Args, "title", "Canvas");
        var alwaysOnTop = GetBoolArg(request.Args, "alwaysOnTop", false);
        
        Logger.Info($"canvas.present: url={url ?? "(html)"}, size={width}x{height}");
        
        PresentRequested?.Invoke(this, new CanvasPresentArgs
        {
            Url = url,
            Html = html,
            Width = width,
            Height = height,
            X = x,
            Y = y,
            Title = title ?? "Canvas",
            AlwaysOnTop = alwaysOnTop
        });
        
        return Task.FromResult(Success(new { presented = true }));
    }
    
    private NodeInvokeResponse HandleHide(NodeInvokeRequest request)
    {
        Logger.Info("canvas.hide");
        HideRequested?.Invoke(this, EventArgs.Empty);
        return Success(new { hidden = true });
    }
    
    private async Task<NodeInvokeResponse> HandleNavigateAsync(NodeInvokeRequest request)
    {
        var url = GetStringArg(request.Args, "url");
        if (string.IsNullOrEmpty(url))
        {
            return Error("Missing url parameter");
        }
        
        Logger.Info($"canvas.navigate: {url}");
        NavigateRequested?.Invoke(this, url);
        
        return Success(new { navigated = true });
    }
    
    private async Task<NodeInvokeResponse> HandleEvalAsync(NodeInvokeRequest request)
    {
        var script = GetStringArg(request.Args, "script")
            ?? GetStringArg(request.Args, "javaScript")
            ?? GetStringArg(request.Args, "javascript");
        if (string.IsNullOrEmpty(script))
        {
            return Error("Missing script parameter");
        }
        
        Logger.Info($"canvas.eval: {script[..Math.Min(50, script.Length)]}...");
        
        if (EvalRequested == null)
        {
            return Error("Canvas not available");
        }
        
        try
        {
            var result = await EvalRequested(script);
            return Success(new { result });
        }
        catch (Exception ex)
        {
            return Error($"Eval failed: {ex.Message}");
        }
    }
    
    private async Task<NodeInvokeResponse> HandleSnapshotAsync(NodeInvokeRequest request)
    {
        var format = GetStringArg(request.Args, "format", "png");
        var maxWidth = Clamp(GetIntArg(request.Args, "maxWidth", 1200), MinSnapshotWidth, MaxSnapshotWidth);
        var quality = Clamp(GetIntArg(request.Args, "quality", 80), MinQuality, MaxQuality);
        
        Logger.Info($"canvas.snapshot: format={format}, maxWidth={maxWidth}");
        
        if (SnapshotRequested == null)
        {
            return Error("Canvas not available");
        }
        
        try
        {
            var base64 = await SnapshotRequested(new CanvasSnapshotArgs
            {
                Format = format ?? "png",
                MaxWidth = maxWidth,
                Quality = quality
            });
            
            return Success(new { format, base64 });
        }
        catch (Exception ex)
        {
            return Error($"Snapshot failed: {ex.Message}");
        }
    }
    
    private NodeInvokeResponse HandleA2UIPush(NodeInvokeRequest request)
    {
        var jsonl = GetStringArg(request.Args, "jsonl");
        var jsonlPath = GetStringArg(request.Args, "jsonlPath");
        var props = request.Args.TryGetProperty("props", out var propsEl) ? propsEl : default;

        if (string.IsNullOrWhiteSpace(jsonl) && !string.IsNullOrWhiteSpace(jsonlPath))
        {
            // Validate jsonlPath to prevent arbitrary file reads.
            // Resolve to absolute path, follow reparse points, and reject anything
            // that doesn't ultimately live inside the system temp directory.
            string fullPath;
            FileInfo fi;
            try
            {
                fullPath = Path.GetFullPath(jsonlPath);
                fi = new FileInfo(fullPath);
                // Resolve symlinks/junctions where possible: a junction inside temp
                // pointing at a user-writable folder elsewhere would otherwise pass
                // the StartsWith check below. (M5 in the unified review.) For
                // non-existent or non-link entries this is a no-op or returns null.
                try
                {
                    var resolved = fi.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved != null) fullPath = resolved.FullName;
                }
                catch { /* non-link, non-existent, or insufficient permission — fall back to the raw path */ }

                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"{request.Command}: jsonlPath outside temp directory: {fullPath}");
                    return Error("jsonlPath must be within the system temp directory");
                }

                if (fi.Exists && fi.Length > MaxA2UIJsonlBytes)
                {
                    Logger.Warn($"{request.Command}: jsonlPath file too large ({fi.Length} > {MaxA2UIJsonlBytes})");
                    return Error($"jsonlPath exceeds maximum size of {MaxA2UIJsonlBytes} bytes");
                }
            }
            catch (Exception ex)
            {
                return Error($"Invalid jsonlPath: {ex.Message}");
            }

            try
            {
                jsonl = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"{request.Command}: failed to read jsonlPath ({jsonlPath})", ex);
                return Error($"Failed to read jsonlPath: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(jsonl))
        {
            return Error("Missing jsonl or jsonlPath parameter");
        }

        // Inline-jsonl size cap. Encoding.UTF8.GetByteCount streams over chars
        // without allocating, so this is cheap.
        long byteCount = System.Text.Encoding.UTF8.GetByteCount(jsonl);
        if (byteCount > MaxA2UIJsonlBytes)
        {
            Logger.Warn($"{request.Command}: jsonl payload too large ({byteCount} > {MaxA2UIJsonlBytes})");
            return Error($"jsonl exceeds maximum size of {MaxA2UIJsonlBytes} bytes");
        }

        // Line-count cap. A push that fans out to thousands of UI-thread
        // dispatches has DoS potential even if individually small.
        int lineCount = CountLines(jsonl);
        if (lineCount > MaxA2UIJsonlLines)
        {
            Logger.Warn($"{request.Command}: jsonl line count too high ({lineCount} > {MaxA2UIJsonlLines})");
            return Error($"jsonl exceeds maximum of {MaxA2UIJsonlLines} lines");
        }

        Logger.Info($"{request.Command}: {byteCount} bytes, {lineCount} lines");

        A2UIPushRequested?.Invoke(this, new CanvasA2UIArgs
        {
            Jsonl = jsonl,
            JsonlPath = jsonlPath,
            Props = props.ValueKind != default ? props.GetRawText() : "{}"
        });

        return Success(new { pushed = true });
    }

    private static int CountLines(string s)
    {
        // Count non-empty newline-delimited lines without allocating an array.
        int count = 0;
        bool inLine = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' || c == '\r')
            {
                if (inLine) { count++; inLine = false; }
            }
            else if (!char.IsWhiteSpace(c))
            {
                inLine = true;
            }
        }
        if (inLine) count++;
        return count;
    }
    
    private NodeInvokeResponse HandleA2UIReset(NodeInvokeRequest request)
    {
        Logger.Info("canvas.a2ui.reset");
        A2UIResetRequested?.Invoke(this, EventArgs.Empty);
        return Success(new { reset = true });
    }

    private async Task<NodeInvokeResponse> HandleA2UIDumpAsync()
    {
        Logger.Info("canvas.a2ui.dump");
        if (A2UIDumpRequested == null)
            return Error("CANVAS_NOT_OPEN: no A2UI canvas is currently active");
        try
        {
            var json = await A2UIDumpRequested();
            // Pass through as a JSON-typed payload so MCP clients see structured data,
            // not a quoted string.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return Success(System.Text.Json.JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()));
        }
        catch (Exception ex)
        {
            return Error($"CANVAS_DUMP_FAILED: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleCapsAsync()
    {
        if (CapsRequested == null)
        {
            return Success(new
            {
                renderer = "none",
                eval = false,
                snapshot = false,
                navigate = false,
                a2ui = new { version = "0.8", introspect = false },
            });
        }
        try
        {
            var json = await CapsRequested();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return Success(System.Text.Json.JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()));
        }
        catch (Exception ex)
        {
            return Error($"CANVAS_CAPS_FAILED: {ex.Message}");
        }
    }
}

public class CanvasPresentArgs : EventArgs
{
    public string? Url { get; set; }
    public string? Html { get; set; }
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public string Title { get; set; } = "Canvas";
    public bool AlwaysOnTop { get; set; }
}

public class CanvasSnapshotArgs
{
    public string Format { get; set; } = "png";
    public int MaxWidth { get; set; } = 1200;
    public int Quality { get; set; } = 80;
}

public class CanvasA2UIArgs : EventArgs
{
    public string? Jsonl { get; set; }
    public string? JsonlPath { get; set; }
    public string Props { get; set; } = "{}";
}
