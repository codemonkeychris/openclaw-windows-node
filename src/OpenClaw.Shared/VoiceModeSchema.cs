using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

public static class VoiceCommands
{
    public const string ListDevices = "voice.devices.list";
    public const string GetSettings = "voice.settings.get";
    public const string SetSettings = "voice.settings.set";
    public const string GetStatus = "voice.status.get";
    public const string Start = "voice.start";
    public const string Stop = "voice.stop";

    private static readonly ReadOnlyCollection<string> s_all = Array.AsReadOnly(
    [
        ListDevices,
        GetSettings,
        SetSettings,
        GetStatus,
        Start,
        Stop
    ]);

    public static IReadOnlyList<string> All => s_all;
}

[JsonConverter(typeof(JsonStringEnumConverter<VoiceActivationMode>))]
public enum VoiceActivationMode
{
    Off,
    WakeWord,
    AlwaysOn
}

[JsonConverter(typeof(JsonStringEnumConverter<VoiceRuntimeState>))]
public enum VoiceRuntimeState
{
    Stopped,
    Paused,
    Idle,
    Arming,
    ListeningForWakeWord,
    ListeningContinuously,
    RecordingUtterance,
    SubmittingAudio,
    PendingManualSend,
    AwaitingResponse,
    PlayingResponse,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter<VoiceChatWindowSubmitMode>))]
public enum VoiceChatWindowSubmitMode
{
    AutoSend,
    WaitForUser
}

public sealed class VoiceSettings
{
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
    public bool Enabled { get; set; }
    public bool ShowConversationToasts { get; set; }
    public string SpeechToTextProviderId { get; set; } = VoiceProviderIds.Windows;
    public string TextToSpeechProviderId { get; set; } = VoiceProviderIds.Windows;
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public int SampleRateHz { get; set; } = 16000;
    public int CaptureChunkMs { get; set; } = 80;
    public bool BargeInEnabled { get; set; } = true;
    public VoiceWakeWordSettings WakeWord { get; set; } = new();
    public VoiceAlwaysOnSettings AlwaysOn { get; set; } = new();
}

public sealed class VoiceWakeWordSettings
{
    public string Engine { get; set; } = "NanoWakeWord";
    public string ModelId { get; set; } = "hey_openclaw";
    public float TriggerThreshold { get; set; } = 0.65f;
    public int TriggerCooldownMs { get; set; } = 2000;
    public int PreRollMs { get; set; } = 1200;
    public int EndSilenceMs { get; set; } = 900;
}

public sealed class VoiceAlwaysOnSettings
{
    public int MinSpeechMs { get; set; } = 250;
    public int EndSilenceMs { get; set; } = 900;
    public int MaxUtteranceMs { get; set; } = 15000;
    public VoiceChatWindowSubmitMode ChatWindowSubmitMode { get; set; } = VoiceChatWindowSubmitMode.AutoSend;
}

public sealed class VoiceAudioDeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsInput { get; set; }
    public bool IsOutput { get; set; }
}

public sealed class VoiceStatusInfo
{
    public bool Available { get; set; }
    public bool Running { get; set; }
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
    public VoiceRuntimeState State { get; set; } = VoiceRuntimeState.Stopped;
    public string? SessionKey { get; set; }
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? WakeWordModelId { get; set; }
    public bool WakeWordLoaded { get; set; }
    public DateTime? LastWakeWordUtc { get; set; }
    public DateTime? LastUtteranceUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class VoiceStartArgs
{
    public VoiceActivationMode? Mode { get; set; }
    public string? SessionKey { get; set; }
}

public sealed class VoiceStopArgs
{
    public string? Reason { get; set; }
}

public sealed class VoiceSettingsUpdateArgs
{
    public VoiceSettings Settings { get; set; } = new();
    public bool Persist { get; set; } = true;
}

public static class VoiceProviderIds
{
    public const string Windows = "windows";
    public const string MiniMax = "minimax";
    public const string ElevenLabs = "elevenlabs";
}

public sealed class VoiceProviderCredentials
{
    public string? MiniMaxApiKey { get; set; }
    public string MiniMaxModel { get; set; } = "speech-2.8-turbo";
    public string MiniMaxVoiceId { get; set; } = "English_MatureBoss";
    public string? ElevenLabsApiKey { get; set; }
    public string? ElevenLabsModel { get; set; }
    public string? ElevenLabsVoiceId { get; set; }
}

public sealed class VoiceProviderOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Runtime { get; set; } = "windows";
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
}

public sealed class VoiceProviderCatalog
{
    public List<VoiceProviderOption> SpeechToTextProviders { get; set; } = [];
    public List<VoiceProviderOption> TextToSpeechProviders { get; set; } = [];
}
