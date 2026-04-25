using System;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.A2UI.Actions;

/// <summary>
/// Sends A2UI actions over the gateway WebSocket as a node-originated
/// notification (method: <c>canvas.a2ui.action</c>). The gateway does not
/// yet broker these to the agent — that wiring is being added in parallel.
/// Until then the message is logged on the gateway side.
/// </summary>
public sealed class GatewayActionTransport : IA2UIActionTransport
{
    private readonly Func<WindowsNodeClient?> _clientProvider;
    private readonly IOpenClawLogger _logger;

    public GatewayActionTransport(Func<WindowsNodeClient?> clientProvider, IOpenClawLogger logger)
    {
        _clientProvider = clientProvider;
        _logger = logger;
    }

    public bool IsAvailable => _clientProvider()?.IsConnected == true;

    public Task DeliverAsync(Protocol.A2UIAction action)
    {
        var client = _clientProvider();
        if (client == null || !client.IsConnected)
            throw new InvalidOperationException("Gateway not connected");

        var envelope = A2UIActionEnvelope.ToEnvelope(action);
        return client.SendCanvasA2UIActionAsync(envelope);
    }
}

/// <summary>
/// Logs the action and stores the last N for diagnostics. Used as a final
/// fallback when no real transport is available, so MCP-only nodes don't
/// silently drop interactions during development.
/// </summary>
public sealed class LoggingActionTransport : IA2UIActionTransport
{
    private readonly IOpenClawLogger _logger;
    public LoggingActionTransport(IOpenClawLogger logger) { _logger = logger; }
    public bool IsAvailable => true;
    public Task DeliverAsync(Protocol.A2UIAction action)
    {
        _logger.Info($"[A2UI] action '{action.Name}' from {action.SourceComponentId ?? "?"} on surface '{action.SurfaceId}' (no remote sink): {A2UIActionEnvelope.Serialize(action)}");
        return Task.CompletedTask;
    }
}
