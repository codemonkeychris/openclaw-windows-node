using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Rendering.Renderers;

public sealed class RowRenderer : IComponentRenderer
{
    public string ComponentName => "Row";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = ctx.Theme.Spacing is { } s ? s : 8,
        };
        ContainerHelpers.ApplyDistribution(sp, c.Properties["distribution"]?.GetValue<string>(), horizontal: true);
        ContainerHelpers.ApplyAlignment(sp, c.Properties["alignment"]?.GetValue<string>(), horizontal: true);

        foreach (var childId in ctx.GetExplicitChildren(c))
        {
            var built = ctx.BuildChild(childId);
            if (built != null) sp.Children.Add(built);
        }
        return sp;
    }
}

public sealed class ColumnRenderer : IComponentRenderer
{
    public string ComponentName => "Column";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = ctx.Theme.Spacing is { } s ? s : 8,
            MinWidth = 0,
        };
        ContainerHelpers.ApplyDistribution(sp, c.Properties["distribution"]?.GetValue<string>(), horizontal: false);
        ContainerHelpers.ApplyAlignment(sp, c.Properties["alignment"]?.GetValue<string>(), horizontal: false);

        foreach (var childId in ctx.GetExplicitChildren(c))
        {
            var built = ctx.BuildChild(childId);
            if (built != null) sp.Children.Add(built);
        }
        return sp;
    }
}

public sealed class ListRenderer : IComponentRenderer
{
    public string ComponentName => "List";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var direction = c.Properties["direction"]?.GetValue<string>() ?? "vertical";
        var orientation = direction.Equals("horizontal", StringComparison.OrdinalIgnoreCase)
            ? Orientation.Horizontal
            : Orientation.Vertical;

        var stack = new StackPanel
        {
            Orientation = orientation,
            Spacing = ctx.Theme.Spacing is { } s ? s : 6,
        };
        ContainerHelpers.ApplyAlignment(stack, c.Properties["alignment"]?.GetValue<string>(), horizontal: orientation == Orientation.Horizontal);

        foreach (var childId in ctx.GetExplicitChildren(c))
        {
            var built = ctx.BuildChild(childId);
            if (built != null) stack.Children.Add(built);
        }

        // Wrap in a ScrollViewer so long lists don't blow out the surface.
        return new ScrollViewer
        {
            HorizontalScrollMode = orientation == Orientation.Horizontal ? ScrollMode.Auto : ScrollMode.Disabled,
            HorizontalScrollBarVisibility = orientation == Orientation.Horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollMode = orientation == Orientation.Vertical ? ScrollMode.Auto : ScrollMode.Disabled,
            VerticalScrollBarVisibility = orientation == Orientation.Vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Content = stack,
        };
    }
}

public sealed class CardRenderer : IComponentRenderer
{
    public string ComponentName => "Card";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(ctx.Theme.CornerRadius is { } r ? r : 8),
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
        };
        try { border.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]; } catch { }
        try { border.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]; } catch { }

        var childId = ctx.GetSingleChild(c, "child");
        if (childId != null) border.Child = ctx.BuildChild(childId);
        return border;
    }
}

public sealed class TabsRenderer : IComponentRenderer
{
    public string ComponentName => "Tabs";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var tabs = new TabView
        {
            IsAddTabButtonVisible = false,
            CanReorderTabs = false,
            CanDragTabs = false,
            TabWidthMode = TabViewWidthMode.SizeToContent,
            CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto,
        };

        if (c.Properties["tabItems"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject tabObj) continue;
                var titleVal = A2UIValue.From(tabObj["title"]);
                var titleText = ctx.ResolveString(titleVal) ?? "Tab";
                var childId = tabObj["child"]?.GetValue<string>();

                var tab = new TabViewItem
                {
                    Header = titleText,
                    IsClosable = false,
                    Content = childId != null ? ctx.BuildChild(childId) : null,
                };

                // Live-bind the title if it's path-bound.
                if (titleVal?.HasPath == true)
                {
                    var subKey = $"tab::{childId ?? Guid.NewGuid().ToString()}::title";
                    void Update() => tab.Header = ctx.ResolveString(titleVal) ?? "Tab";
                    ctx.WatchValue(c.Id, subKey, titleVal, Update);
                }

                tabs.TabItems.Add(tab);
            }
        }
        return tabs;
    }
}

public sealed class ModalRenderer : IComponentRenderer
{
    public string ComponentName => "Modal";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        // v1: render as inline Expander showing the entry-point child as the
        // header and the content child as the body. A real Window-level dialog
        // is preferred long-term but requires XAML-root threading we don't yet
        // have plumbed for arbitrary surface trees.
        var entryId = ctx.GetSingleChild(c, "entryPointChild");
        var contentId = ctx.GetSingleChild(c, "contentChild");

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (entryId != null) expander.Header = ctx.BuildChild(entryId);
        if (contentId != null) expander.Content = ctx.BuildChild(contentId);
        return expander;
    }
}

public sealed class DividerRenderer : IComponentRenderer
{
    public string ComponentName => "Divider";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var axis = c.Properties["axis"]?.GetValue<string>() ?? "horizontal";
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle();
        if (axis.Equals("vertical", StringComparison.OrdinalIgnoreCase))
        {
            rect.Width = 1;
            rect.HorizontalAlignment = HorizontalAlignment.Center;
            rect.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            rect.Height = 1;
            rect.HorizontalAlignment = HorizontalAlignment.Stretch;
            rect.VerticalAlignment = VerticalAlignment.Center;
        }
        rect.Margin = new Thickness(0, 4, 0, 4);
        try { rect.Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]; }
        catch { rect.Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray); }
        return rect;
    }
}

internal static class ContainerHelpers
{
    public static void ApplyDistribution(StackPanel sp, string? value, bool horizontal)
    {
        if (value == null) return;
        // StackPanel doesn't have a true justify-content; map to alignment of the panel itself.
        if (horizontal)
        {
            sp.HorizontalAlignment = value switch
            {
                "start" => HorizontalAlignment.Left,
                "center" => HorizontalAlignment.Center,
                "end" => HorizontalAlignment.Right,
                "spaceBetween" or "spaceAround" or "spaceEvenly" => HorizontalAlignment.Stretch,
                _ => sp.HorizontalAlignment,
            };
        }
        else
        {
            sp.VerticalAlignment = value switch
            {
                "start" => VerticalAlignment.Top,
                "center" => VerticalAlignment.Center,
                "end" => VerticalAlignment.Bottom,
                "spaceBetween" or "spaceAround" or "spaceEvenly" => VerticalAlignment.Stretch,
                _ => sp.VerticalAlignment,
            };
        }
    }

    public static void ApplyAlignment(StackPanel sp, string? value, bool horizontal)
    {
        if (value == null) return;
        // Cross-axis alignment.
        if (horizontal)
        {
            sp.VerticalAlignment = value switch
            {
                "start" => VerticalAlignment.Top,
                "center" => VerticalAlignment.Center,
                "end" => VerticalAlignment.Bottom,
                "stretch" => VerticalAlignment.Stretch,
                _ => sp.VerticalAlignment,
            };
        }
        else
        {
            sp.HorizontalAlignment = value switch
            {
                "start" => HorizontalAlignment.Left,
                "center" => HorizontalAlignment.Center,
                "end" => HorizontalAlignment.Right,
                "stretch" => HorizontalAlignment.Stretch,
                _ => sp.HorizontalAlignment,
            };
        }
    }
}
