using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Theming;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// One implementation per A2UI v0.8 component type. Catalog-strict: registry
/// is populated at construction time. To extend, add a renderer class and
/// register it in <see cref="ComponentRendererRegistry.BuildDefault"/>.
/// </summary>
public interface IComponentRenderer
{
    /// <summary>The A2UI component name (case-sensitive — matches the wire), e.g. "Button", "Column".</summary>
    string ComponentName { get; }

    /// <summary>
    /// Build a XAML element for the component. Renderers are responsible for
    /// resolving their own children via <see cref="RenderContext.BuildChild"/>;
    /// there is no central adjacency walker. Bindings to the data model
    /// register subscriptions in <see cref="RenderContext.Subscriptions"/> so
    /// they can be torn down on rebuild.
    /// </summary>
    FrameworkElement Render(A2UIComponentDef component, RenderContext ctx);
}

/// <summary>
/// Per-render-call context. Renderers hold no state across calls; the
/// surface host owns lifetime and rebuilds the tree when the root or
/// component definitions change.
/// </summary>
public sealed class RenderContext
{
    public required string SurfaceId { get; init; }
    public required DataModelObservable DataModel { get; init; }
    public required IActionSink Actions { get; init; }
    public required A2UITheme Theme { get; init; }
    public required Func<string, FrameworkElement?> BuildChild { get; init; }
    public required IDictionary<string, IDisposable> Subscriptions { get; init; }

    /// <summary>
    /// Pull the named property from the component as an A2UI value (tagged
    /// union of literals + path). Returns null if the property is absent.
    /// </summary>
    public A2UIValue? GetValue(A2UIComponentDef c, string propertyKey) =>
        A2UIValue.From(c.Properties[propertyKey]);

    /// <summary>Read the current resolved string for a value.</summary>
    public string? ResolveString(A2UIValue? value)
    {
        if (value == null) return null;
        if (value.LiteralString != null) return value.LiteralString;
        if (value.LiteralNumber.HasValue) return value.LiteralNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.LiteralBoolean.HasValue) return value.LiteralBoolean.Value ? "true" : "false";
        if (value.HasPath)
        {
            var node = DataModel.Read(value.Path!);
            if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
            return node?.ToString();
        }
        return null;
    }

    public double? ResolveNumber(A2UIValue? value)
    {
        if (value == null) return null;
        if (value.LiteralNumber.HasValue) return value.LiteralNumber.Value;
        if (value.HasPath)
        {
            var node = DataModel.Read(value.Path!);
            if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
            if (node is JsonValue jv2 && jv2.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    public bool? ResolveBoolean(A2UIValue? value)
    {
        if (value == null) return null;
        if (value.LiteralBoolean.HasValue) return value.LiteralBoolean.Value;
        if (value.HasPath)
        {
            var node = DataModel.Read(value.Path!);
            if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        }
        return null;
    }

    /// <summary>Subscribe a UI update to changes on a value's path. No-op for literals.</summary>
    public void WatchValue(string componentId, string subKey, A2UIValue? value, Action update)
    {
        if (value == null || !value.HasPath) return;
        var key = $"{componentId}::{subKey}";
        if (Subscriptions.TryGetValue(key, out var prev))
        {
            try { prev.Dispose(); } catch { }
            Subscriptions.Remove(key);
        }
        Subscriptions[key] = DataModel.Subscribe(value.Path!, update);
    }

    /// <summary>
    /// Read children IDs from a container's <c>children</c> property
    /// (<c>{ "explicitList": [...] }</c>). Template form is not yet supported;
    /// it returns an empty list and logs once at the router level.
    /// </summary>
    public IReadOnlyList<string> GetExplicitChildren(A2UIComponentDef c, string key = "children")
    {
        if (c.Properties[key] is not JsonObject child) return Array.Empty<string>();
        if (child["explicitList"] is JsonArray arr)
        {
            var list = new List<string>(arr.Count);
            foreach (var item in arr)
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s)) list.Add(s);
            return list;
        }
        return Array.Empty<string>();
    }

    public string? GetSingleChild(A2UIComponentDef c, string key)
    {
        if (c.Properties[key] is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return null;
    }

    /// <summary>Build the action context array → flat object payload.</summary>
    public JsonObject? BuildActionContext(JsonNode? actionNode)
    {
        if (actionNode is not JsonObject actionObj) return null;
        if (actionObj["context"] is not JsonArray ctxArr) return null;
        var result = new JsonObject();
        foreach (var item in ctxArr)
        {
            if (item is not JsonObject e) continue;
            var key = e["key"] is JsonValue kj && kj.TryGetValue<string>(out var k) ? k : null;
            if (key == null) continue;
            var val = A2UIValue.From(e["value"]);
            if (val == null) { result[key] = null; continue; }
            if (val.LiteralString != null) result[key] = val.LiteralString;
            else if (val.LiteralNumber.HasValue) result[key] = val.LiteralNumber.Value;
            else if (val.LiteralBoolean.HasValue) result[key] = val.LiteralBoolean.Value;
            else if (val.HasPath) result[key] = DataModel.Read(val.Path!)?.DeepClone();
        }
        return result;
    }
}
