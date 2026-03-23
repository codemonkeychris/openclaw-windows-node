using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public class VoiceCommandsTests
{
    [Fact]
    public void All_ContainsExpectedCommandsInStableOrder()
    {
        Assert.Equal(
        [
            "voice.devices.list",
            "voice.settings.get",
            "voice.settings.set",
            "voice.status.get",
            "voice.start",
            "voice.stop"
        ],
        VoiceCommands.All);
    }
}

public class VoiceSchemaDefaultsTests
{
    [Fact]
    public void VoiceSettings_Defaults_AreConcreteAndProviderAgnostic()
    {
        var settings = new VoiceSettings();

        Assert.False(settings.Enabled);
        Assert.Equal(VoiceActivationMode.Off, settings.Mode);
        Assert.False(settings.ShowConversationToasts);
        Assert.Equal(VoiceProviderIds.Windows, settings.SpeechToTextProviderId);
        Assert.Equal(VoiceProviderIds.Windows, settings.TextToSpeechProviderId);
        Assert.Equal(16000, settings.SampleRateHz);
        Assert.Equal(80, settings.CaptureChunkMs);
        Assert.True(settings.BargeInEnabled);
        Assert.Equal("NanoWakeWord", settings.WakeWord.Engine);
        Assert.Equal("hey_openclaw", settings.WakeWord.ModelId);
        Assert.Equal(0.65f, settings.WakeWord.TriggerThreshold);
        Assert.Equal(250, settings.AlwaysOn.MinSpeechMs);
        Assert.Equal(VoiceChatWindowSubmitMode.AutoSend, settings.AlwaysOn.ChatWindowSubmitMode);
    }

    [Fact]
    public void VoiceStatusInfo_Defaults_ToStopped()
    {
        var status = new VoiceStatusInfo();

        Assert.False(status.Available);
        Assert.False(status.Running);
        Assert.Equal(VoiceActivationMode.Off, status.Mode);
        Assert.Equal(VoiceRuntimeState.Stopped, status.State);
        Assert.False(status.WakeWordLoaded);
        Assert.Null(status.LastError);
    }

    [Fact]
    public void VoiceEnums_Serialize_AsStrings()
    {
        var json = JsonSerializer.Serialize(new VoiceStartArgs
        {
            Mode = VoiceActivationMode.WakeWord
        });

        Assert.Contains("\"WakeWord\"", json);
    }

    [Fact]
    public void VoiceProviderCatalog_Defaults_ToEmptyLists()
    {
        var catalog = new VoiceProviderCatalog();

        Assert.Empty(catalog.SpeechToTextProviders);
        Assert.Empty(catalog.TextToSpeechProviders);
    }
}
