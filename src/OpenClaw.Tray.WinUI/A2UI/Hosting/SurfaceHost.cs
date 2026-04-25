using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.A2UI.Theming;

namespace OpenClawTray.A2UI.Hosting;

/// <summary>
/// One per active surface. Owns the component definition table, rebuilds the
/// XAML tree from the declared root, and exposes a single root element that
/// the canvas window slots into a content host.
///
/// Lifecycle in v0.8:
///   surfaceUpdate (defs come in)  → ApplyComponents
///   beginRendering (root + style) → BeginRendering, triggers Build
///   dataModelUpdate               → store applies; subscribed renderers refresh
/// A re-issued surfaceUpdate with the same surfaceId patches in place.
/// </summary>
public sealed class SurfaceHost : IDisposable
{
    private readonly DataModelObservable _dataModel;
    private readonly ComponentRendererRegistry _registry;
    private readonly IActionSink _actions;
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, A2UIComponentDef> _defs = new(StringComparer.Ordinal);
    private readonly Grid _root;
    private A2UITheme _theme;
    private string? _rootId;

    public string SurfaceId { get; }
    public string? Title { get; private set; }
    public FrameworkElement RootElement => _root;

    public SurfaceHost(
        string surfaceId,
        DataModelObservable dataModel,
        ComponentRendererRegistry registry,
        IActionSink actions)
    {
        SurfaceId = surfaceId;
        _dataModel = dataModel;
        _registry = registry;
        _actions = actions;
        _theme = A2UITheme.Empty;
        _root = new Grid { Padding = new Thickness(16) };
    }

    /// <summary>
    /// Add or replace components in the definition table. If a root has
    /// already been declared, rebuild the visual tree.
    /// </summary>
    public void ApplyComponents(IReadOnlyList<A2UIComponentDef> components)
    {
        foreach (var def in components) _defs[def.Id] = def;
        if (_rootId != null) Rebuild();
    }

    /// <summary>
    /// Declare which component is the root and apply surface-level styles.
    /// Triggers an immediate rebuild.
    /// </summary>
    public void BeginRendering(string rootId, System.Text.Json.Nodes.JsonObject? styles)
    {
        _rootId = rootId;
        _theme = A2UITheme.Parse(styles);
        Title = null;
        ApplyThemeToScope(_root, _theme);
        Rebuild();
    }

    public void Dispose()
    {
        DisposeSubscriptions();
        _defs.Clear();
        _root.Children.Clear();
    }

    /// <summary>
    /// JSON snapshot of this surface's logical state — components (id +
    /// componentName + properties), declared root, and the current data
    /// model tree. Used by <c>canvas.a2ui.dump</c> for headless verification.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject GetSnapshot()
    {
        var components = new System.Text.Json.Nodes.JsonArray();
        foreach (var def in _defs.Values)
        {
            var entry = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = def.Id,
                ["componentName"] = def.ComponentName,
                ["properties"] = def.Properties.DeepClone(),
            };
            if (def.Weight is { } w) entry["weight"] = w;
            components.Add(entry);
        }
        return new System.Text.Json.Nodes.JsonObject
        {
            ["surfaceId"] = SurfaceId,
            ["root"] = _rootId,
            ["components"] = components,
            ["dataModel"] = _dataModel.Root.DeepClone(),
        };
    }

    private void Rebuild()
    {
        DisposeSubscriptions();
        _root.Children.Clear();
        if (_rootId == null) return;

        var built = BuildElement(_rootId);
        if (built != null) _root.Children.Add(built);
    }

    private FrameworkElement? BuildElement(string id)
    {
        if (!_defs.TryGetValue(id, out var def))
            return null;

        var renderer = _registry.GetOrUnknown(def.ComponentName);
        var ctx = new RenderContext
        {
            SurfaceId = SurfaceId,
            DataModel = _dataModel,
            Actions = _actions,
            Theme = _theme,
            BuildChild = BuildElement,
            Subscriptions = _subscriptions,
        };

        try { return renderer.Render(def, ctx); }
        catch (Exception)
        {
            // Renderer failure should never crash the surface; fall back to unknown.
            return _registry.GetOrUnknown("__error__").Render(def, ctx);
        }
    }

    private void DisposeSubscriptions()
    {
        foreach (var s in _subscriptions.Values)
        {
            try { s.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }

    private static void ApplyThemeToScope(FrameworkElement element, A2UITheme theme)
    {
        if (theme == A2UITheme.Empty) return;

        var resources = element.Resources;
        if (theme.Accent is { } accent)
        {
            resources["A2UIAccentBrush"] = new SolidColorBrush(accent);
            resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        }
        if (theme.Foreground is { } fg)
            resources["A2UIForegroundBrush"] = new SolidColorBrush(fg);
        if (theme.FontFamily is { } font && !string.IsNullOrWhiteSpace(font))
            resources["A2UIFontFamily"] = new FontFamily(font);
    }
}
