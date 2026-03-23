using OpenClaw.Shared;
using OpenClawTray.Services.Voice;

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
        Assert.False(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.ElevenLabs));
    }
}
