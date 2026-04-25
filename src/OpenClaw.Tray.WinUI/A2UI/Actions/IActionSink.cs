using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Actions;

/// <summary>
/// Single seam through which actions leave the renderer. Implementations
/// route to the gateway WebSocket, MCP notifications, or a fallback queue.
/// </summary>
public interface IActionSink
{
    void Raise(A2UIAction action);
}

/// <summary>
/// Routes actions to one of N transports based on availability. The first
/// transport whose <see cref="IA2UIActionTransport.IsAvailable"/> returns
/// true wins. If none are available, actions go to an in-memory fallback
/// queue that drains on the next available transport.
/// </summary>
public sealed class ActionDispatcher : IActionSink
{
    private readonly IReadOnlyList<IA2UIActionTransport> _transports;
    private readonly IOpenClawLogger _logger;
    private readonly ConcurrentQueue<A2UIAction> _fallback = new();
    private readonly Dictionary<string, DateTimeOffset> _lastDelivery = new();
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(200);
    private readonly object _debounceLock = new();

    public ActionDispatcher(IReadOnlyList<IA2UIActionTransport> transports, IOpenClawLogger logger)
    {
        _transports = transports;
        _logger = logger;
    }

    public void Raise(A2UIAction action)
    {
        if (IsDebounced(action)) return;
        _ = SendAsync(action);
    }

    private bool IsDebounced(A2UIAction action)
    {
        var key = $"{action.SurfaceId}|{action.SourceComponentId}|{action.Name}";
        var now = DateTimeOffset.UtcNow;
        lock (_debounceLock)
        {
            if (_lastDelivery.TryGetValue(key, out var last) && (now - last) < DebounceWindow)
                return true;
            _lastDelivery[key] = now;
        }
        return false;
    }

    private async Task SendAsync(A2UIAction action)
    {
        // Drain any backlog first so order is preserved.
        while (_fallback.TryPeek(out var pending))
        {
            if (await TryDeliverAsync(pending))
            {
                _fallback.TryDequeue(out _);
            }
            else
            {
                break;
            }
        }

        if (!await TryDeliverAsync(action))
        {
            _logger.Warn($"[A2UI] No transport available; queued action '{action.Name}' for later delivery.");
            _fallback.Enqueue(action);
        }
    }

    private async Task<bool> TryDeliverAsync(A2UIAction action)
    {
        foreach (var t in _transports)
        {
            if (!t.IsAvailable) continue;
            try
            {
                await t.DeliverAsync(action);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[A2UI] Transport '{t.GetType().Name}' failed: {ex.Message}");
            }
        }
        return false;
    }
}

/// <summary>One concrete delivery channel.</summary>
public interface IA2UIActionTransport
{
    bool IsAvailable { get; }
    Task DeliverAsync(A2UIAction action);
}

/// <summary>
/// Helper: shared envelope serialization. v0.8 client→server shape.
/// </summary>
public static class A2UIActionEnvelope
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonObject ToEnvelope(A2UIAction a)
    {
        var inner = new JsonObject
        {
            ["name"] = a.Name,
            ["surfaceId"] = a.SurfaceId,
            ["timestamp"] = a.Timestamp.ToString("o"),
        };
        if (!string.IsNullOrEmpty(a.SourceComponentId))
            inner["sourceComponentId"] = a.SourceComponentId;
        if (a.Context != null)
            inner["context"] = (JsonNode)a.Context.DeepClone();

        return new JsonObject { ["action"] = inner };
    }

    public static string Serialize(A2UIAction a) => ToEnvelope(a).ToJsonString(s_options);
}
