using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services.Voice;

namespace OpenClaw.Tray.Tests;

public class VoiceServiceTransportTests
{
    [Fact]
    public void GetOrCreateTransportReadySource_ReusesExistingTaskWhileConnecting()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Connecting, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.Same(existing, result);
        Assert.False((bool)arguments[2]!);
    }

    [Fact]
    public void GetOrCreateTransportReadySource_CreatesFreshTaskWhenDisconnected()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Disconnected, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.NotSame(existing, result);
        Assert.True((bool)arguments[2]!);
    }

    [Fact]
    public void GetOrCreateTransportReadySource_CreatesFreshTaskAfterError()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Error, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.NotSame(existing, result);
        Assert.True((bool)arguments[2]!);
    }

    [Fact]
    public void UsesCloudTextToSpeechRuntime_ReturnsTrueForWebSocketProviders()
    {
        var method = typeof(VoiceService).GetMethod(
            "UsesCloudTextToSpeechRuntime",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var provider = new VoiceProviderOption
        {
            Id = VoiceProviderIds.MiniMax,
            TextToSpeechWebSocket = new VoiceTextToSpeechWebSocketContract
            {
                EndpointTemplate = "wss://example.test/tts"
            }
        };

        var result = (bool)method.Invoke(null, [provider])!;

        Assert.True(result);
    }

    [Theory]
    [InlineData(true, false, 0, true)]
    [InlineData(false, true, 0, true)]
    [InlineData(false, false, 1, true)]
    [InlineData(false, false, 0, false)]
    public void ShouldAcceptAssistantReply_MatchesPlaybackAndAwaitingState(
        bool awaitingReply,
        bool isSpeaking,
        int queuedReplyCount,
        bool expected)
    {
        var method = typeof(VoiceService).GetMethod(
            "ShouldAcceptAssistantReply",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (bool)method.Invoke(null, [awaitingReply, isSpeaking, queuedReplyCount])!;

        Assert.Equal(expected, result);
    }

    private static MethodInfo GetMethod()
    {
        return typeof(VoiceService).GetMethod(
            "GetOrCreateTransportReadySource",
            BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}
