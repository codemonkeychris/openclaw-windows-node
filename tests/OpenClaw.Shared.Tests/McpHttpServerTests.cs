using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Mcp;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Verifies the McpHttpServer security gate and protocol envelope. Each test
/// boots the server on an ephemeral port so they can run in parallel and we
/// don't collide with the production 8765.
/// </summary>
public class McpHttpServerTests
{
    private sealed class FakeCapability : INodeCapability
    {
        public string Category => "alpha";
        public IReadOnlyList<string> Commands => new[] { "alpha.echo" };
        public bool CanHandle(string command) => command == "alpha.echo";
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
            => Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = new { echoed = request.Command } });
    }

    private static int FreePort()
    {
        // Bind to port 0, ask the kernel for the assigned port, release.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (McpHttpServer server, HttpClient client, Uri url) Boot()
    {
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { new FakeCapability() });
        var server = new McpHttpServer(bridge, port, NullLogger.Instance);
        server.Start();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        return (server, http, new Uri($"http://127.0.0.1:{port}/"));
    }

    [Fact]
    public async Task Get_ReturnsFriendlyProbe()
    {
        var (server, http, _) = Boot();
        try
        {
            var resp = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("OpenClaw MCP server", body);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_ValidJsonRpc_ReturnsResult()
    {
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithBrowserOrigin_RejectedWithForbidden()
    {
        // The CSRF gate: any Origin header means a browser is the caller.
        // Real MCP clients do not send Origin.
        var (server, http, _) = Boot();
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                    Encoding.UTF8, "application/json"),
            };
            msg.Headers.Add("Origin", "https://evil.com");
            var resp = await http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithRebindHost_RejectedWithForbidden()
    {
        // DNS rebinding: attacker hostname masking 127.0.0.1.
        var (server, http, _) = Boot();
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                    Encoding.UTF8, "application/json"),
            };
            msg.Headers.Host = "evil.com";
            var resp = await http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithLocalhostHost_Accepted()
    {
        var (server, http, _) = Boot();
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                    Encoding.UTF8, "application/json"),
            };
            msg.Headers.Host = "localhost";
            var resp = await http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithTextPlain_RejectedWithUnsupportedMediaType()
    {
        // CORS-simple POST defense: text/plain bypasses preflight, so we
        // require application/json explicitly.
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                Encoding.UTF8, "text/plain");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_WithJsonAndCharset_Accepted()
    {
        // application/json with charset suffix should still be accepted.
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}",
                Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Put_RejectedWithMethodNotAllowed()
    {
        var (server, http, _) = Boot();
        try
        {
            var resp = await http.PutAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Post_OversizedBody_RejectedWithRequestTooLarge()
    {
        var (server, http, _) = Boot();
        try
        {
            // 5 MiB exceeds the 4 MiB cap.
            var big = new string('x', 5 * 1024 * 1024);
            var content = new StringContent(big, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public async Task Notification_Returns204NoContent()
    {
        var (server, http, _) = Boot();
        try
        {
            var content = new StringContent(
                @"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}",
                Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("/", content);
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        finally { server.Dispose(); http.Dispose(); }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (server, http, _) = Boot();
        try
        {
            server.Dispose();
            server.Dispose(); // must not throw
        }
        finally { http.Dispose(); }
    }

    [Fact]
    public void Ctor_NullBridge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new McpHttpServer(null!, 1234, NullLogger.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        Assert.Throws<ArgumentNullException>(() => new McpHttpServer(bridge, 1234, null!));
    }
}
