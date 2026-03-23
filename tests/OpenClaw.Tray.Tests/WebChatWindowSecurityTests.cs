using System.Reflection;
using OpenClawTray.Windows;

namespace OpenClaw.Tray.Tests;

public class WebChatWindowSecurityTests
{
    [Fact]
    public void TrustedVoiceSubmit_AllowsExpectedOriginAndNonce()
    {
        var method = GetTrustedSubmitMethod();
        var arguments = new object?[]
        {
            """{"type":"voice-manual-submit","text":"hello world","nonce":"expected-nonce"}""",
            "https://chat.example.test/path?x=1",
            "https://chat.example.test",
            "expected-nonce",
            null
        };

        var accepted = (bool)method.Invoke(null, arguments)!;

        Assert.True(accepted);
        Assert.Equal("hello world", arguments[4]);
    }

    [Fact]
    public void TrustedVoiceSubmit_RejectsUnexpectedOrigin()
    {
        var method = GetTrustedSubmitMethod();
        var arguments = new object?[]
        {
            """{"type":"voice-manual-submit","text":"hello world","nonce":"expected-nonce"}""",
            "https://evil.example.test/",
            "https://chat.example.test",
            "expected-nonce",
            null
        };

        var accepted = (bool)method.Invoke(null, arguments)!;

        Assert.False(accepted);
        Assert.Equal(string.Empty, arguments[4]);
    }

    [Fact]
    public void TrustedVoiceSubmit_RejectsUnexpectedNonce()
    {
        var method = GetTrustedSubmitMethod();
        var arguments = new object?[]
        {
            """{"type":"voice-manual-submit","text":"hello world","nonce":"wrong-nonce"}""",
            "https://chat.example.test/",
            "https://chat.example.test",
            "expected-nonce",
            null
        };

        var accepted = (bool)method.Invoke(null, arguments)!;

        Assert.False(accepted);
        Assert.Equal(string.Empty, arguments[4]);
    }

    private static MethodInfo GetTrustedSubmitMethod()
    {
        return typeof(WebChatWindow).GetMethod(
            "TryExtractTrustedVoiceManualSubmit",
            BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}
