using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class WindowsNodeClientTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>
    /// Invokes the protected ProcessMessageAsync method via reflection and awaits the returned task.
    /// Simulates a raw message arriving from the gateway without requiring a live WebSocket.
    /// </summary>
    private static async Task InvokeProcessMessageAsync(WindowsNodeClient client, string json)
    {
        var method = typeof(WindowsNodeClient).GetMethod(
            "ProcessMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(client, [json])!;
        await task;
    }

    /// <summary>A minimal capability that handles "test.cmd" for dispatch tests.</summary>
    private sealed class StubCapability : NodeCapabilityBase
    {
        public override string Category => "test";
        private static readonly string[] _cmds = { "test.cmd" };
        public override System.Collections.Generic.IReadOnlyList<string> Commands => _cmds;
        public bool WasInvoked { get; private set; }

        public StubCapability() : base(NullLogger.Instance) { }

        public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        {
            WasInvoked = true;
            return Task.FromResult(Success(new { ok = true }));
        }
    }


    [Theory]
    [InlineData("http://localhost:18789", "ws://localhost:18789")]
    [InlineData("https://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("wss://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    public void Constructor_NormalizesGatewayUrl(string inputUrl, string expectedUrl)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient(inputUrl, "test-token", dataPath);
            var field = typeof(WindowsNodeClient).BaseType?.GetField(
                "_gatewayUrl",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var actualUrl = field?.GetValue(client) as string;

            Assert.Equal(expectedUrl, actualUrl);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    /// <summary>
    /// Regression test: when hello-ok includes auth.deviceToken, PairingStatusChanged must
    /// fire exactly once — not twice (once from the token block and again from the DeviceToken
    /// fallback check that follows it).
    /// </summary>
    [Fact]
    public void HandleResponse_HelloOkWithDeviceToken_FiresPairingChangedExactlyOnce()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            // Put client into pending-approval state (simulates first-connect, no stored token)
            var isPendingField = typeof(WindowsNodeClient).GetField(
                "_isPendingApproval",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(isPendingField);
            isPendingField!.SetValue(client, true);

            var pairingEvents = new List<PairingStatusEventArgs>();
            client.PairingStatusChanged += (_, e) => pairingEvents.Add(e);

            // Build a hello-ok payload that includes auth.deviceToken
            var json = """
                {
                    "type": "res",
                    "ok": true,
                    "payload": {
                        "type": "hello-ok",
                        "nodeId": "test-node-id",
                        "auth": {
                            "deviceToken": "test-device-token-abc123"
                        }
                    }
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleResponseMethod);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Single(pairingEvents);
            Assert.Equal(PairingStatus.Paired, pairingEvents[0].Status);
            Assert.Equal("Pairing approved!", pairingEvents[0].Message);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    /// <summary>
    /// When hello-ok has no token and no stored token, fires exactly one Pending event.
    /// </summary>
    [Fact]
    public void HandleResponse_HelloOkNoToken_FiresPendingExactlyOnce()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-node-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var pairingEvents = new List<PairingStatusEventArgs>();
            client.PairingStatusChanged += (_, e) => pairingEvents.Add(e);

            var json = """
                {
                    "type": "res",
                    "ok": true,
                    "payload": {
                        "type": "hello-ok",
                        "nodeId": "test-node-id"
                    }
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse",
                BindingFlags.NonPublic | BindingFlags.Instance);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Single(pairingEvents);
            Assert.Equal(PairingStatus.Pending, pairingEvents[0].Status);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }
        }
    }

    /// <summary>
    /// When hello-ok arrives and the device already has a stored token (was previously paired),
    /// PairingStatusChanged fires exactly once with Paired status and a null message.
    /// </summary>
    [Fact]
    public void HandleResponse_HelloOkWithAlreadyStoredToken_FiresPairedWithNullMessage()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            // Simulate the device having been approved before by storing a device token
            var deviceIdentityField = typeof(WindowsNodeClient).GetField(
                "_deviceIdentity", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(deviceIdentityField);
            var deviceIdentity = (DeviceIdentity)deviceIdentityField!.GetValue(client)!;
            deviceIdentity.StoreDeviceToken("previously-stored-device-token");

            var pairingEvents = new List<PairingStatusEventArgs>();
            client.PairingStatusChanged += (_, e) => pairingEvents.Add(e);

            var json = """
                {
                    "type": "res",
                    "ok": true,
                    "payload": {
                        "type": "hello-ok",
                        "nodeId": "test-node-id"
                    }
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleResponseMethod);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Single(pairingEvents);
            Assert.Equal(PairingStatus.Paired, pairingEvents[0].Status);
            Assert.Null(pairingEvents[0].Message); // No message when already paired
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>
    /// When a response with ok=false arrives, StatusChanged fires with Error status.
    /// </summary>
    [Fact]
    public void HandleResponse_ErrorResponseOkFalse_RaisesErrorStatus()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var statusChanges = new List<ConnectionStatus>();
            client.StatusChanged += (_, s) => statusChanges.Add(s);

            // A response with payload present but ok=false triggers the error path
            var json = """
                {
                    "type": "res",
                    "ok": false,
                    "payload": {},
                    "error": {
                        "message": "Invalid token",
                        "code": "auth.invalid"
                    }
                }
                """;
            var root = JsonDocument.Parse(json).RootElement;

            var handleResponseMethod = typeof(WindowsNodeClient).GetMethod(
                "HandleResponse", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleResponseMethod);
            handleResponseMethod!.Invoke(client, [root]);

            Assert.Contains(ConnectionStatus.Error, statusChanges);
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>
    /// RegisterCapability adds the capability's category and all its commands to the
    /// internal registration object, making them available for advertisement to the gateway.
    /// </summary>
    [Fact]
    public void RegisterCapability_AddsCategoryAndCommandsToRegistration()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            var capability = new StubCapability();

            client.RegisterCapability(capability);

            Assert.Contains(capability, client.Capabilities);

            var regField = typeof(WindowsNodeClient).GetField(
                "_registration", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(regField);
            var reg = (NodeRegistration)regField!.GetValue(client)!;

            Assert.Contains("test", reg.Capabilities);
            Assert.Contains("test.cmd", reg.Commands);
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>
    /// Registering the same capability twice does not duplicate its category or commands
    /// in the registration object.
    /// </summary>
    [Fact]
    public void RegisterCapability_DoesNotDuplicateCategoryOrCommands_WhenAddedTwice()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            var capability = new StubCapability();

            client.RegisterCapability(capability);
            client.RegisterCapability(capability);

            var regField = typeof(WindowsNodeClient).GetField(
                "_registration", BindingFlags.NonPublic | BindingFlags.Instance);
            var reg = (NodeRegistration)regField!.GetValue(client)!;

            Assert.Equal(1, reg.Capabilities.Count(c => c == "test"));
            Assert.Equal(1, reg.Commands.Count(c => c == "test.cmd"));
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>SetPermission stores the permission value in the node registration.</summary>
    [Fact]
    public void SetPermission_StoresPermissionValue()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            client.SetPermission("system.run", true);
            client.SetPermission("camera", false);

            var regField = typeof(WindowsNodeClient).GetField(
                "_registration", BindingFlags.NonPublic | BindingFlags.Instance);
            var reg = (NodeRegistration)regField!.GetValue(client)!;

            Assert.True(reg.Permissions["system.run"]);
            Assert.False(reg.Permissions["camera"]);
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>Invalid JSON is silently ignored — no exception propagates to the caller.</summary>
    [Fact]
    public async Task ProcessMessageAsync_InvalidJson_IsIgnoredGracefully()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            // Should not throw
            await InvokeProcessMessageAsync(client, "this is not valid json {{{");
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>A message with an unrecognised 'type' field is silently ignored.</summary>
    [Fact]
    public async Task ProcessMessageAsync_UnknownMessageType_IsIgnoredGracefully()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            await InvokeProcessMessageAsync(client, """{"type":"xyzzy","payload":{}}""");
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>
    /// A node.invoke request for a command that has no registered capability handler does
    /// not throw; the client logs a warning and attempts to send an error response (which
    /// silently drops when there is no live WebSocket).
    /// </summary>
    [Fact]
    public async Task ProcessMessageAsync_ReqNodeInvoke_UnregisteredCommand_IsHandledWithoutException()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            // No capabilities registered

            var json = """
                {
                    "type": "req",
                    "id": "req-1",
                    "method": "node.invoke",
                    "params": {
                        "command": "unknown.command",
                        "args": {}
                    }
                }
                """;

            // Should not throw
            await InvokeProcessMessageAsync(client, json);
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>
    /// A node.invoke request whose command contains invalid characters (spaces, semicolons)
    /// is rejected gracefully without throwing.
    /// </summary>
    [Fact]
    public async Task ProcessMessageAsync_ReqNodeInvoke_InvalidCommandFormat_IsHandledWithoutException()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var json = """
                {
                    "type": "req",
                    "id": "req-2",
                    "method": "node.invoke",
                    "params": {
                        "command": "bad command; rm -rf /",
                        "args": {}
                    }
                }
                """;

            await InvokeProcessMessageAsync(client, json);
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>
    /// A node.invoke request for a registered capability dispatches to that capability
    /// and fires the InvokeReceived event exactly once.
    /// </summary>
    [Fact]
    public async Task ProcessMessageAsync_ReqNodeInvoke_ValidCommand_DispatchesToCapabilityAndFiresEvent()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);
            var stub = new StubCapability();
            client.RegisterCapability(stub);

            var invokeEvents = new List<NodeInvokeRequest>();
            client.InvokeReceived += (_, req) => invokeEvents.Add(req);

            var json = """
                {
                    "type": "req",
                    "id": "req-3",
                    "method": "node.invoke",
                    "params": {
                        "command": "test.cmd",
                        "args": { "foo": "bar" }
                    }
                }
                """;

            await InvokeProcessMessageAsync(client, json);

            Assert.True(stub.WasInvoked);
            Assert.Single(invokeEvents);
            Assert.Equal("test.cmd", invokeEvents[0].Command);
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }

    /// <summary>
    /// A node.invoke.request event (different from req type) with no command is handled
    /// gracefully — no exception propagates.
    /// </summary>
    [Fact]
    public async Task ProcessMessageAsync_EventNodeInvokeRequest_MissingCommand_IsHandledWithoutException()
    {
        var dataPath = CreateTempDir();
        try
        {
            using var client = new WindowsNodeClient("ws://localhost:18789", "test-token", dataPath);

            var json = """
                {
                    "type": "event",
                    "event": "node.invoke.request",
                    "payload": {
                        "requestId": "evt-1"
                    }
                }
                """;

            await InvokeProcessMessageAsync(client, json);
        }
        finally
        {
            DeleteTempDir(dataPath);
        }
    }
}
