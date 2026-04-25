using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Hosting;
using OpenClawTray.A2UI.Rendering;
using WinUIEx;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenClawTray.Windows;

/// <summary>
/// Native A2UI canvas. Hosts an <see cref="A2UIRouter"/> that drives one or
/// more <see cref="SurfaceHost"/> instances directly into XAML. No WebView2,
/// no HTTP host, no JS bridge.
/// </summary>
public sealed partial class A2UICanvasWindow : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int SW_SHOWNORMAL = 1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly DispatcherQueue _dispatcher;
    private readonly A2UIRouter _router;
    private readonly DataModelStore _dataModel;

    public bool IsClosed { get; private set; }

    /// <summary>
    /// Construct the native A2UI canvas window. <paramref name="actions"/> is
    /// the dispatcher used for any user interactions raised by surface widgets.
    /// </summary>
    public A2UICanvasWindow(IActionSink actions, MediaResolver media, IOpenClawLogger logger)
    {
        InitializeComponent();
        this.SetIcon("Assets\\openclaw.ico");
        Closed += (_, _) => IsClosed = true;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _dataModel = new DataModelStore(_dispatcher);
        var registry = ComponentRendererRegistry.BuildDefault(media);
        _router = new A2UIRouter(_dispatcher, _dataModel, registry, actions, logger);

        _router.SurfaceCreated += OnSurfaceCreated;
        _router.SurfaceDeleted += OnSurfaceDeleted;
    }

    /// <summary>
    /// Push a JSONL blob through the router. Safe from any thread — the
    /// router posts UI work to the dispatcher.
    /// </summary>
    public void Push(string jsonl) => _router.Push(jsonl);

    /// <summary>Reset everything: surfaces, data models, visuals.</summary>
    public void Reset() => _router.ResetAll();

    private void OnSurfaceCreated(object? sender, SurfaceHost host)
    {
        UpdateLayout();
    }

    private void OnSurfaceDeleted(object? sender, string surfaceId)
    {
        UpdateLayout();
    }

    private void UpdateLayout()
    {
        var surfaces = new List<SurfaceHost>(_router.Surfaces.Values);
        if (surfaces.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            SingleSurfaceHost.Visibility = Visibility.Collapsed;
            MultiSurfaceTabs.Visibility = Visibility.Collapsed;
            SingleSurfaceHost.Content = null;
            MultiSurfaceTabs.TabItems.Clear();
            Title = "Canvas";
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;

        if (surfaces.Count == 1)
        {
            var s = surfaces[0];
            SingleSurfaceHost.Visibility = Visibility.Visible;
            MultiSurfaceTabs.Visibility = Visibility.Collapsed;
            SingleSurfaceHost.Content = s.RootElement;
            MultiSurfaceTabs.TabItems.Clear();
            Title = string.IsNullOrWhiteSpace(s.Title) ? "Canvas" : s.Title!;
        }
        else
        {
            SingleSurfaceHost.Visibility = Visibility.Collapsed;
            MultiSurfaceTabs.Visibility = Visibility.Visible;
            SingleSurfaceHost.Content = null;
            MultiSurfaceTabs.TabItems.Clear();
            foreach (var s in surfaces)
            {
                var tab = new TabViewItem
                {
                    Header = string.IsNullOrWhiteSpace(s.Title) ? s.SurfaceId : s.Title!,
                    Content = s.RootElement,
                    IsClosable = false,
                };
                MultiSurfaceTabs.TabItems.Add(tab);
            }
            Title = "Canvas";
        }
    }

    /// <summary>
    /// Render the current visible content (Empty / Single / Multi mode) into a
    /// PNG or JPEG and return base64. Uses XAML's RenderTargetBitmap so the
    /// snapshot reflects the actual visual tree, not a Win32 window blit.
    /// </summary>
    public async Task<string> CaptureSnapshotAsync(string format = "png")
    {
        // Pick the visible host. Falls back to the root grid if neither is shown
        // (e.g. surfaces have been pushed but layout hasn't settled).
        FrameworkElement? target = null;
        if (SingleSurfaceHost.Visibility == Visibility.Visible)
            target = SingleSurfaceHost;
        else if (MultiSurfaceTabs.Visibility == Visibility.Visible)
            target = MultiSurfaceTabs;
        else if (EmptyPanel.Visibility == Visibility.Visible)
            target = EmptyPanel;

        if (target == null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            target = RootGrid;

        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(target);
        var pixelBuffer = await rtb.GetPixelsAsync();
        var pixels = pixelBuffer.ToArray();

        var encoderId = format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) || format.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            ? BitmapEncoder.JpegEncoderId
            : BitmapEncoder.PngEncoderId;

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        var dpi = 96.0; // RenderTargetBitmap is logical-pixel sized; encode at 1:1.
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            dpi,
            dpi,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// JSON state dump: surfaces (with components + data model) keyed by id.
    /// Returned as a string so callers can hand it back through MCP without
    /// further serialization. Reflects the renderer's authoritative state at
    /// call time — components that haven't been rendered yet are still listed.
    /// </summary>
    public string GetStateSnapshot()
    {
        var surfaces = new JsonObject();
        foreach (var (id, host) in _router.Surfaces)
        {
            surfaces[id] = host.GetSnapshot();
        }
        var snapshot = new JsonObject
        {
            ["renderer"] = "native",
            ["a2uiVersion"] = "0.8",
            ["surfaceCount"] = _router.Surfaces.Count,
            ["surfaces"] = surfaces,
        };
        return snapshot.ToJsonString();
    }

    public void BringToFront(bool keepTopMost = false)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_SHOWNORMAL);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
            if (!keepTopMost)
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }
        catch { /* best-effort */ }
    }
}
