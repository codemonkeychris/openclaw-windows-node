using OpenClaw.Shared;
using OpenClawTray.Services.Voice;
using System.Linq;

namespace OpenClaw.Tray.Tests;

public class VoiceProviderCatalogServiceTests
{
    [Fact]
    public void LoadCatalog_IncludesBuiltInMiniMaxAndElevenLabsTtsProviders()
    {
        var catalog = VoiceProviderCatalogService.LoadCatalog();

        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.Windows);
        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.MiniMax);
        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.ElevenLabs);
    }

    [Fact]
    public void SupportsTextToSpeechRuntime_ReturnsTrueForMiniMaxOnlyWhenImplemented()
    {
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.Windows));
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.MiniMax));
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.ElevenLabs));
    }

    [Fact]
    public void LoadCatalog_ExposesBuiltInCloudTtsContracts()
    {
        var catalog = VoiceProviderCatalogService.LoadCatalog();

        var minimax = Assert.Single(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.MiniMax);
        Assert.NotNull(minimax.TextToSpeechHttp);
        Assert.Equal("https://api.minimax.io/v1/t2a_v2", minimax.TextToSpeechHttp!.EndpointTemplate);
        Assert.Equal("Authorization", minimax.TextToSpeechHttp.AuthenticationHeaderName);
        Assert.Equal(VoiceTextToSpeechResponseModes.HexJsonString, minimax.TextToSpeechHttp.ResponseAudioMode);
        Assert.Equal("speech-2.8-turbo", minimax.Settings.Single(s => s.Key == VoiceProviderSettingKeys.Model).DefaultValue);
        Assert.Equal("English_MatureBoss", minimax.Settings.Single(s => s.Key == VoiceProviderSettingKeys.VoiceId).DefaultValue);

        var elevenLabs = Assert.Single(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.ElevenLabs);
        Assert.NotNull(elevenLabs.TextToSpeechHttp);
        Assert.Equal(
            "https://api.elevenlabs.io/v1/text-to-speech/{{voiceId}}?output_format=mp3_44100_128",
            elevenLabs.TextToSpeechHttp!.EndpointTemplate);
        Assert.Equal("xi-api-key", elevenLabs.TextToSpeechHttp.AuthenticationHeaderName);
        Assert.Equal(VoiceTextToSpeechResponseModes.Binary, elevenLabs.TextToSpeechHttp.ResponseAudioMode);
        Assert.Equal("eleven_multilingual_v2", elevenLabs.Settings.Single(s => s.Key == VoiceProviderSettingKeys.Model).DefaultValue);
    }
}
