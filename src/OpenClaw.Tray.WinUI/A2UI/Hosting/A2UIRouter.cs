using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.A2UI.Theming;

namespace OpenClawTray.A2UI.Hosting;

/// <summary>
/// Stateful per-window router. Parses inbound JSONL, dispatches to surface
/// hosts on the UI thread, and exposes events for the window to react to
/// (surface lifecycle).
///
/// Designed for single-window/multiple-surfaces — the spec leaves room for a
/// future multi-window mode but the v1 host stacks surfaces in TabView slots.
/// </summary>
public sealed class A2UIRouter
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DataModelStore _dataModel;
    private readonly ComponentRendererRegistry _registry;
    private readonly IActionSink _actions;
    private readonly IOpenClawLogger _logger;
    private readonly Dictionary<string, SurfaceHost> _surfaces = new(StringComparer.Ordinal);

    public event EventHandler<SurfaceHost>? SurfaceCreated;
    public event EventHandler<SurfaceHost>? SurfaceRendered;
    public event EventHandler<string>? SurfaceDeleted;

    public A2UIRouter(
        DispatcherQueue dispatcher,
        DataModelStore dataModel,
        ComponentRendererRegistry registry,
        IActionSink actions,
        IOpenClawLogger logger)
    {
        _dispatcher = dispatcher;
        _dataModel = dataModel;
        _registry = registry;
        _actions = actions;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, SurfaceHost> Surfaces => _surfaces;

    /// <summary>Push a JSONL blob. Each line is parsed independently.</summary>
    public void Push(string jsonl)
    {
        foreach (var msg in A2UIMessageParser.Parse(jsonl))
        {
            DispatchOnUI(msg);
        }
    }

    public void ResetAll()
    {
        DispatchToUI(() =>
        {
            foreach (var s in _surfaces.Values)
            {
                try { s.Dispose(); } catch { }
                SurfaceDeleted?.Invoke(this, s.SurfaceId);
            }
            _surfaces.Clear();
            _dataModel.RemoveAll();
            _logger.Info("[A2UI] reset all surfaces");
        });
    }

    private void DispatchOnUI(A2UIMessage msg)
    {
        DispatchToUI(() =>
        {
            try { Apply(msg); }
            catch (Exception ex) { _logger.Error("[A2UI] Router apply failed", ex); }
        });
    }

    private void DispatchToUI(Action action)
    {
        if (_dispatcher.HasThreadAccess) { action(); return; }
        _dispatcher.TryEnqueue(() => action());
    }

    private void Apply(A2UIMessage msg)
    {
        switch (msg)
        {
            case SurfaceUpdateMessage su:
            {
                var host = GetOrCreateSurface(su.SurfaceId);
                host.ApplyComponents(su.Components);
                _logger.Info($"[A2UI] surfaceUpdate '{su.SurfaceId}' ({su.Components.Count} component(s))");
                break;
            }

            case BeginRenderingMessage br:
            {
                var host = GetOrCreateSurface(br.SurfaceId);
                host.BeginRendering(br.Root, br.Styles);
                SurfaceRendered?.Invoke(this, host);
                _logger.Info($"[A2UI] beginRendering '{br.SurfaceId}' root='{br.Root}' (catalog={br.CatalogId ?? "default"})");
                break;
            }

            case DataModelUpdateMessage dmu:
            {
                _dataModel.ApplyDataModelUpdate(dmu.SurfaceId, dmu.Path, dmu.Contents);
                _logger.Debug($"[A2UI] dataModelUpdate '{dmu.SurfaceId}' path='{dmu.Path ?? "/"}' ({dmu.Contents.Count} entry(ies))");
                break;
            }

            case DeleteSurfaceMessage ds:
            {
                if (_surfaces.TryGetValue(ds.SurfaceId, out var existing))
                {
                    existing.Dispose();
                    _surfaces.Remove(ds.SurfaceId);
                    _dataModel.Remove(ds.SurfaceId);
                    SurfaceDeleted?.Invoke(this, ds.SurfaceId);
                    _logger.Info($"[A2UI] deleteSurface '{ds.SurfaceId}'");
                }
                break;
            }

            case UnknownEnvelopeMessage ue:
                _logger.Warn($"[A2UI] Unknown envelope kind '{ue.Kind}'; skipping");
                break;
        }
    }

    private SurfaceHost GetOrCreateSurface(string surfaceId)
    {
        if (_surfaces.TryGetValue(surfaceId, out var existing)) return existing;

        var observable = _dataModel.GetOrCreate(surfaceId);
        var host = new SurfaceHost(surfaceId, observable, _registry, _actions);
        _surfaces[surfaceId] = host;
        SurfaceCreated?.Invoke(this, host);
        return host;
    }
}
