using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;

namespace OpenClawTray.Services;

public sealed class VoiceService : IDisposable
{
    private const int HResultSpeechPrivacyDeclined = unchecked((int)0x80045509);
    private static readonly TimeSpan TransportConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DuplicateTranscriptWindow = TimeSpan.FromSeconds(2);

    private readonly IOpenClawLogger _logger;
    private readonly SettingsManager _settings;
    private readonly object _gate = new();

    private VoiceStatusInfo _status;
    private VoiceActivationMode? _runtimeModeOverride;
    private CancellationTokenSource? _runtimeCts;
    private OpenClawGatewayClient? _chatClient;
    private ConnectionStatus _chatTransportStatus = ConnectionStatus.Disconnected;
    private TaskCompletionSource<bool>? _transportReadyTcs;
    private SpeechRecognizer? _speechRecognizer;
    private SpeechSynthesizer? _speechSynthesizer;
    private MediaPlayer? _mediaPlayer;
    private bool _recognitionActive;
    private bool _awaitingReply;
    private bool _isSpeaking;
    private string? _lastTranscript;
    private DateTime _lastTranscriptUtc;
    private bool _disposed;

    public VoiceService(IOpenClawLogger logger, SettingsManager settings)
    {
        _logger = logger;
        _settings = settings;
        _status = new VoiceStatusInfo();
        _status = BuildStoppedStatus(null, null);
    }

    public VoiceStatusInfo CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return Clone(_status);
            }
        }
    }

    public Task<VoiceSettings> GetSettingsAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(Clone(_settings.Voice));
        }
    }

    public Task<VoiceSettings> UpdateSettingsAsync(VoiceSettingsUpdateArgs update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            _settings.Voice = Clone(update.Settings);
            if (update.Persist)
            {
                _settings.Save();
            }

            if (_status.Running)
            {
                _status = BuildRunningStatus(
                    _runtimeModeOverride ?? _settings.Voice.Mode,
                    _status.SessionKey,
                    _status.State,
                    _status.LastError);
                _status.LastUtteranceUtc = _status.LastUtteranceUtc;
                _status.LastWakeWordUtc = _status.LastWakeWordUtc;
            }
            else
            {
                _status = BuildStoppedStatus(_status.SessionKey, _status.LastError);
            }

            return Task.FromResult(Clone(_settings.Voice));
        }
    }

    public Task<VoiceStatusInfo> GetStatusAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(Clone(_status));
        }
    }

    public async Task<VoiceStatusInfo> StartAsync(VoiceStartArgs args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        args ??= new VoiceStartArgs();

        VoiceSettings effectiveSettings;
        VoiceActivationMode requestedMode;
        string? sessionKey;

        lock (_gate)
        {
            effectiveSettings = Clone(_settings.Voice);
            requestedMode = args.Mode ?? effectiveSettings.Mode;
            sessionKey = args.SessionKey ?? _status.SessionKey;

            if (args.Mode.HasValue && args.Mode.Value != VoiceActivationMode.Off)
            {
                effectiveSettings.Enabled = true;
                effectiveSettings.Mode = args.Mode.Value;
                _runtimeModeOverride = args.Mode.Value;
            }
            else if (args.Mode == VoiceActivationMode.Off)
            {
                _runtimeModeOverride = null;
            }

            if (!effectiveSettings.Enabled || requestedMode == VoiceActivationMode.Off)
            {
                _status = BuildStoppedStatus(sessionKey, "Voice mode is disabled");
                return Clone(_status);
            }
        }

        await StopRuntimeResourcesAsync(updateStoppedStatus: false);

        try
        {
            switch (requestedMode)
            {
                case VoiceActivationMode.AlwaysOn:
                    await StartAlwaysOnRuntimeAsync(effectiveSettings, sessionKey);
                    break;
                case VoiceActivationMode.WakeWord:
                    lock (_gate)
                    {
                        _status = BuildRunningStatus(
                            VoiceActivationMode.WakeWord,
                            sessionKey,
                            VoiceRuntimeState.ListeningForWakeWord,
                            "WakeWord capture is not implemented yet");
                    }
                    _logger.Info("Voice runtime started in mode WakeWord");
                    break;
                default:
                    lock (_gate)
                    {
                        _status = BuildStoppedStatus(sessionKey, "Voice mode is disabled");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Voice runtime start failed", ex);
            lock (_gate)
            {
                _status = BuildErrorStatus(requestedMode, sessionKey, GetUserFacingErrorMessage(ex));
            }
        }

        return CurrentStatus;
    }

    public async Task<VoiceStatusInfo> StopAsync(VoiceStopArgs args)
    {
        args ??= new VoiceStopArgs();

        await StopRuntimeResourcesAsync(updateStoppedStatus: false);

        lock (_gate)
        {
            _runtimeModeOverride = null;
            _status = BuildStoppedStatus(_status.SessionKey, args.Reason);
            _logger.Info($"Voice runtime stopped{(string.IsNullOrWhiteSpace(args.Reason) ? string.Empty : $": {args.Reason}")}");
            return Clone(_status);
        }
    }

    public async Task<VoiceAudioDeviceInfo[]> ListDevicesAsync()
    {
        try
        {
            var inputDefaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            var outputDefaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            var results = new List<VoiceAudioDeviceInfo>();

            var inputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            foreach (var device in inputDevices)
            {
                results.Add(new VoiceAudioDeviceInfo
                {
                    DeviceId = device.Id,
                    Name = device.Name,
                    IsDefault = string.Equals(device.Id, inputDefaultId, StringComparison.Ordinal),
                    IsInput = true
                });
            }

            var outputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
            foreach (var device in outputDevices)
            {
                results.Add(new VoiceAudioDeviceInfo
                {
                    DeviceId = device.Id,
                    Name = device.Name,
                    IsDefault = string.Equals(device.Id, outputDefaultId, StringComparison.Ordinal),
                    IsOutput = true
                });
            }

            return results
                .OrderByDescending(d => d.IsDefault)
                .ThenBy(d => d.IsInput ? 0 : 1)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice device enumeration failed: {ex.Message}");
            return
            [
                new VoiceAudioDeviceInfo
                {
                    DeviceId = "default-input",
                    Name = "System default microphone",
                    IsDefault = true,
                    IsInput = true
                },
                new VoiceAudioDeviceInfo
                {
                    DeviceId = "default-output",
                    Name = "System default speaker",
                    IsDefault = true,
                    IsOutput = true
                }
            ];
        }
    }

    public VoiceProviderCatalog GetProviderCatalog()
    {
        return VoiceProviderCatalogService.LoadCatalog(_logger);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = StopRuntimeResourcesAsync(updateStoppedStatus: true);
    }

    private async Task StartAlwaysOnRuntimeAsync(VoiceSettings settings, string? sessionKey)
    {
        var selectedSpeechToText = VoiceProviderCatalogService.ResolveSpeechToTextProvider(
            settings.SpeechToTextProviderId,
            _logger);
        var selectedTextToSpeech = VoiceProviderCatalogService.ResolveTextToSpeechProvider(
            settings.TextToSpeechProviderId,
            _logger);
        var fallbackMessage = BuildProviderFallbackMessage(selectedSpeechToText, selectedTextToSpeech);

        await EnsureMicrophoneConsentAsync();

        var runtimeCts = new CancellationTokenSource();
        var recognizer = await CreateSpeechRecognizerAsync(settings);
        var synthesizer = new SpeechSynthesizer();
        var player = new MediaPlayer();

        if (!string.IsNullOrWhiteSpace(settings.InputDeviceId))
        {
            _logger.Warn("Selected input device is saved, but AlwaysOn currently uses the system speech input device.");
        }

        if (!string.IsNullOrWhiteSpace(settings.OutputDeviceId))
        {
            _logger.Warn("Selected output device is saved, but AlwaysOn currently uses the default speech output device.");
        }

        recognizer.ContinuousRecognitionSession.ResultGenerated += OnSpeechResultGenerated;
        recognizer.ContinuousRecognitionSession.Completed += OnSpeechRecognitionCompleted;

        lock (_gate)
        {
            _runtimeCts = runtimeCts;
            _speechRecognizer = recognizer;
            _speechSynthesizer = synthesizer;
            _mediaPlayer = player;
            _status = BuildRunningStatus(
                VoiceActivationMode.AlwaysOn,
                sessionKey,
                VoiceRuntimeState.Arming,
                fallbackMessage);
        }

        await EnsureChatTransportAsync(runtimeCts.Token);
        await StartRecognitionSessionAsync();

        lock (_gate)
        {
            if (_status.Running)
            {
                _status = BuildRunningStatus(
                    VoiceActivationMode.AlwaysOn,
                    sessionKey,
                    VoiceRuntimeState.ListeningContinuously,
                    fallbackMessage);
            }
        }

        _logger.Info("Voice runtime started in mode AlwaysOn");
    }

    private async Task<SpeechRecognizer> CreateSpeechRecognizerAsync(VoiceSettings settings)
    {
        var recognizer = new SpeechRecognizer();
        recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(settings.AlwaysOn.EndSilenceMs);
        recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(10);
        recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(4);
        recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "always-on-dictation"));

        var compilation = await recognizer.CompileConstraintsAsync();
        if (compilation.Status != SpeechRecognitionResultStatus.Success)
        {
            recognizer.Dispose();
            throw new InvalidOperationException($"Speech recognizer unavailable: {compilation.Status}");
        }

        return recognizer;
    }

    private async Task EnsureMicrophoneConsentAsync()
    {
        if (!PackageHelper.IsPackaged)
        {
            return;
        }

        using var capture = new MediaCapture();
        var initSettings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Audio,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        };

        await capture.InitializeAsync(initSettings);
    }

    private async Task EnsureChatTransportAsync(CancellationToken cancellationToken)
    {
        OpenClawGatewayClient? existingClient;
        ConnectionStatus existingStatus;

        lock (_gate)
        {
            existingClient = _chatClient;
            existingStatus = _chatTransportStatus;
            if (existingStatus == ConnectionStatus.Connected)
            {
                return;
            }

            _transportReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (existingClient == null)
            {
                _chatClient = new OpenClawGatewayClient(_settings.GatewayUrl, _settings.Token, _logger);
                _chatClient.StatusChanged += OnChatTransportStatusChanged;
                _chatClient.NotificationReceived += OnChatNotificationReceived;
                existingClient = _chatClient;
                _chatTransportStatus = ConnectionStatus.Connecting;
            }
        }

        if (existingStatus == ConnectionStatus.Disconnected || existingClient != _chatClient)
        {
            await existingClient!.ConnectAsync();
        }

        Task readyTask;
        lock (_gate)
        {
            readyTask = _transportReadyTcs?.Task ?? Task.CompletedTask;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TransportConnectTimeout);

        var completed = await Task.WhenAny(readyTask, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
        if (completed != readyTask)
        {
            throw new TimeoutException("Timed out connecting voice chat transport.");
        }

        await readyTask;
    }

    private async Task StartRecognitionSessionAsync()
    {
        SpeechRecognizer? recognizer;

        lock (_gate)
        {
            recognizer = _speechRecognizer;
            if (recognizer == null || _recognitionActive)
            {
                return;
            }
        }

        await recognizer.ContinuousRecognitionSession.StartAsync();

        lock (_gate)
        {
            _recognitionActive = true;
            if (_status.Running && !_awaitingReply && !_isSpeaking)
            {
                _status = BuildRunningStatus(
                    VoiceActivationMode.AlwaysOn,
                    _status.SessionKey,
                    VoiceRuntimeState.ListeningContinuously,
                    null);
                _status.LastUtteranceUtc = _status.LastUtteranceUtc;
            }
        }
    }

    private async Task StopRecognitionSessionAsync()
    {
        SpeechRecognizer? recognizer;

        lock (_gate)
        {
            recognizer = _speechRecognizer;
            if (recognizer == null || !_recognitionActive)
            {
                return;
            }

            _recognitionActive = false;
        }

        try
        {
            await recognizer.ContinuousRecognitionSession.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice recognition stop failed: {ex.Message}");
        }
    }

    private async void OnSpeechResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        try
        {
            var result = args.Result;
            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (result.Status != SpeechRecognitionResultStatus.Success ||
                result.Confidence == SpeechRecognitionConfidence.Rejected)
            {
                return;
            }

            await HandleRecognizedTextAsync(text);
        }
        catch (Exception ex)
        {
            _logger.Error("Voice recognition handler failed", ex);
            lock (_gate)
            {
                if (_status.Running)
                {
                    _status = BuildErrorStatus(VoiceActivationMode.AlwaysOn, _status.SessionKey, GetUserFacingErrorMessage(ex));
                }
            }
        }
    }

    private async Task HandleRecognizedTextAsync(string text)
    {
        CancellationToken cancellationToken;

        lock (_gate)
        {
            if (_runtimeCts == null || _status.Mode != VoiceActivationMode.AlwaysOn || !_status.Running)
            {
                return;
            }

            if (_awaitingReply || _isSpeaking)
            {
                return;
            }

            if (string.Equals(text, _lastTranscript, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - _lastTranscriptUtc < DuplicateTranscriptWindow)
            {
                return;
            }

            _lastTranscript = text;
            _lastTranscriptUtc = DateTime.UtcNow;
            _awaitingReply = true;
            _status = BuildRunningStatus(
                VoiceActivationMode.AlwaysOn,
                _status.SessionKey,
                VoiceRuntimeState.AwaitingResponse,
                _status.LastError);
            _status.LastUtteranceUtc = DateTime.UtcNow;
            cancellationToken = _runtimeCts.Token;
        }

        await StopRecognitionSessionAsync();

        try
        {
            await EnsureChatTransportAsync(cancellationToken);

            OpenClawGatewayClient? client;
            lock (_gate)
            {
                client = _chatClient;
            }

            if (client == null)
            {
                throw new InvalidOperationException("Voice chat transport is unavailable.");
            }

            _logger.Info($"Voice transcript captured: {text}");
            await client.SendChatMessageAsync(text);
            _ = MonitorReplyTimeoutAsync(text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error("Voice transcript submit failed", ex);

            lock (_gate)
            {
                _awaitingReply = false;
                _status = BuildRunningStatus(
                    VoiceActivationMode.AlwaysOn,
                    _status.SessionKey,
                    VoiceRuntimeState.ListeningContinuously,
                    GetUserFacingErrorMessage(ex));
            }

            await StartRecognitionSessionAsync();
        }
    }

    private async Task MonitorReplyTimeoutAsync(string transcript, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReplyTimeout, cancellationToken);

            var shouldResume = false;
            lock (_gate)
            {
                if (_awaitingReply &&
                    string.Equals(_lastTranscript, transcript, StringComparison.OrdinalIgnoreCase))
                {
                    _awaitingReply = false;
                    _status = BuildRunningStatus(
                        VoiceActivationMode.AlwaysOn,
                        _status.SessionKey,
                        VoiceRuntimeState.ListeningContinuously,
                        "Timed out waiting for an assistant reply.");
                    _status.LastUtteranceUtc = _status.LastUtteranceUtc;
                    shouldResume = true;
                }
            }

            if (shouldResume)
            {
                await StartRecognitionSessionAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnChatNotificationReceived(object? sender, OpenClawNotification notification)
    {
        if (!notification.IsChat || string.IsNullOrWhiteSpace(notification.Message))
        {
            return;
        }

        string text;

        lock (_gate)
        {
            if (!_awaitingReply || !_status.Running || _status.Mode != VoiceActivationMode.AlwaysOn)
            {
                return;
            }

            _awaitingReply = false;
            _isSpeaking = true;
                _status = BuildRunningStatus(
                    VoiceActivationMode.AlwaysOn,
                    _status.SessionKey,
                    VoiceRuntimeState.PlayingResponse,
                    _status.LastError);
            text = notification.Message;
        }

        try
        {
            await SpeakTextAsync(text);
        }
        catch (Exception ex)
        {
            _logger.Error("Voice reply playback failed", ex);
            lock (_gate)
            {
                _status = BuildRunningStatus(
                    VoiceActivationMode.AlwaysOn,
                    _status.SessionKey,
                    VoiceRuntimeState.ListeningContinuously,
                    GetUserFacingErrorMessage(ex));
            }
        }
        finally
        {
            lock (_gate)
            {
                _isSpeaking = false;
                if (_status.Running)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.AlwaysOn,
                        _status.SessionKey,
                        VoiceRuntimeState.ListeningContinuously,
                        _status.LastError);
                    _status.LastUtteranceUtc = _status.LastUtteranceUtc;
                }
            }

            try
            {
                await StartRecognitionSessionAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Voice recognition resume failed: {ex.Message}");
            }
        }
    }

    private async Task SpeakTextAsync(string text)
    {
        SpeechSynthesizer? synthesizer;
        MediaPlayer? player;

        lock (_gate)
        {
            synthesizer = _speechSynthesizer;
            player = _mediaPlayer;
        }

        if (synthesizer == null || player == null)
        {
            throw new InvalidOperationException("Speech playback is not ready.");
        }

        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        var playbackEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        TypedEventHandler<MediaPlayer, object>? endedHandler = null;
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? failedHandler = null;

        endedHandler = (sender, _) => playbackEnded.TrySetResult(true);
        failedHandler = (sender, args) => playbackEnded.TrySetException(new InvalidOperationException(args.ErrorMessage));

        player.MediaEnded += endedHandler;
        player.MediaFailed += failedHandler;

        try
        {
            player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            player.Play();
            await playbackEnded.Task;
        }
        finally
        {
            player.MediaEnded -= endedHandler;
            player.MediaFailed -= failedHandler;
            player.Source = null;
        }
    }

    private async void OnSpeechRecognitionCompleted(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        try
        {
            CancellationToken token;
            var shouldRestart = false;

            lock (_gate)
            {
                if (_runtimeCts == null || _runtimeCts.IsCancellationRequested)
                {
                    return;
                }

                _recognitionActive = false;
                token = _runtimeCts.Token;
                shouldRestart = _status.Running &&
                                _status.Mode == VoiceActivationMode.AlwaysOn &&
                                !_awaitingReply &&
                                !_isSpeaking;
            }

            if (shouldRestart && !token.IsCancellationRequested)
            {
                await Task.Delay(250, token);
                await StartRecognitionSessionAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice recognition completion handler failed: {ex.Message}");
        }
    }

    private void OnChatTransportStatusChanged(object? sender, ConnectionStatus status)
    {
        lock (_gate)
        {
            _chatTransportStatus = status;

            if (status == ConnectionStatus.Connected)
            {
                _transportReadyTcs?.TrySetResult(true);

                if (_status.Running &&
                    _status.Mode == VoiceActivationMode.AlwaysOn &&
                    !_awaitingReply &&
                    !_isSpeaking)
                {
                _status = BuildRunningStatus(
                    VoiceActivationMode.AlwaysOn,
                    _status.SessionKey,
                    VoiceRuntimeState.ListeningContinuously,
                    _status.LastError);
                _status.LastUtteranceUtc = _status.LastUtteranceUtc;
            }
            }
            else if (status == ConnectionStatus.Error)
            {
                _transportReadyTcs?.TrySetException(
                    new InvalidOperationException("Voice chat transport failed to connect."));

                if (_status.Running && _status.Mode == VoiceActivationMode.AlwaysOn)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.AlwaysOn,
                        _status.SessionKey,
                        VoiceRuntimeState.Arming,
                        "Voice chat transport failed.");
                    _status.LastUtteranceUtc = _status.LastUtteranceUtc;
                }
            }
            else if (status == ConnectionStatus.Disconnected)
            {
                if (_status.Running && _status.Mode == VoiceActivationMode.AlwaysOn)
                {
                    _status = BuildRunningStatus(
                        VoiceActivationMode.AlwaysOn,
                        _status.SessionKey,
                        VoiceRuntimeState.Arming,
                        "Voice chat transport disconnected.");
                    _status.LastUtteranceUtc = _status.LastUtteranceUtc;
                }
            }
        }
    }

    private async Task StopRuntimeResourcesAsync(bool updateStoppedStatus)
    {
        CancellationTokenSource? runtimeCts;
        OpenClawGatewayClient? chatClient;
        SpeechRecognizer? recognizer;
        SpeechSynthesizer? synthesizer;
        MediaPlayer? player;
        var sessionKey = CurrentStatus.SessionKey;

        lock (_gate)
        {
            runtimeCts = _runtimeCts;
            _runtimeCts = null;

            chatClient = _chatClient;
            _chatClient = null;
            _chatTransportStatus = ConnectionStatus.Disconnected;
            _transportReadyTcs = null;

            recognizer = _speechRecognizer;
            _speechRecognizer = null;
            _recognitionActive = false;

            synthesizer = _speechSynthesizer;
            _speechSynthesizer = null;

            player = _mediaPlayer;
            _mediaPlayer = null;

            _awaitingReply = false;
            _isSpeaking = false;
        }

        try { runtimeCts?.Cancel(); } catch { }

        if (recognizer != null)
        {
            recognizer.ContinuousRecognitionSession.ResultGenerated -= OnSpeechResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed -= OnSpeechRecognitionCompleted;

            try { await recognizer.ContinuousRecognitionSession.CancelAsync(); } catch { }
            try { recognizer.Dispose(); } catch { }
        }

        if (player != null)
        {
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            try { player.Dispose(); } catch { }
        }

        try { synthesizer?.Dispose(); } catch { }

        if (chatClient != null)
        {
            chatClient.StatusChanged -= OnChatTransportStatusChanged;
            chatClient.NotificationReceived -= OnChatNotificationReceived;
            try { await chatClient.DisconnectAsync(); } catch { }
            try { chatClient.Dispose(); } catch { }
        }

        try { runtimeCts?.Dispose(); } catch { }

        if (updateStoppedStatus)
        {
            lock (_gate)
            {
                _status = BuildStoppedStatus(sessionKey, "Disposed");
            }
        }
    }

    private VoiceStatusInfo BuildRunningStatus(
        VoiceActivationMode mode,
        string? sessionKey,
        VoiceRuntimeState state,
        string? lastError)
    {
        var settings = _settings.Voice;
        return new VoiceStatusInfo
        {
            Available = true,
            Running = true,
            Mode = mode,
            State = state,
            SessionKey = sessionKey,
            InputDeviceId = settings.InputDeviceId,
            OutputDeviceId = settings.OutputDeviceId,
            WakeWordModelId = settings.WakeWord.ModelId,
            WakeWordLoaded = mode == VoiceActivationMode.WakeWord,
            LastWakeWordUtc = _status.LastWakeWordUtc,
            LastUtteranceUtc = _status.LastUtteranceUtc,
            LastError = lastError
        };
    }

    private VoiceStatusInfo BuildStoppedStatus(string? sessionKey, string? reason)
    {
        var settings = _settings.Voice;
        return new VoiceStatusInfo
        {
            Available = true,
            Running = false,
            Mode = _runtimeModeOverride ?? settings.Mode,
            State = VoiceRuntimeState.Stopped,
            SessionKey = sessionKey,
            InputDeviceId = settings.InputDeviceId,
            OutputDeviceId = settings.OutputDeviceId,
            WakeWordModelId = settings.WakeWord.ModelId,
            WakeWordLoaded = false,
            LastWakeWordUtc = _status.LastWakeWordUtc,
            LastUtteranceUtc = _status.LastUtteranceUtc,
            LastError = reason
        };
    }

    private VoiceStatusInfo BuildErrorStatus(VoiceActivationMode mode, string? sessionKey, string? reason)
    {
        var status = BuildRunningStatus(mode, sessionKey, VoiceRuntimeState.Error, reason);
        status.Running = false;
        return status;
    }

    private static VoiceSettings Clone(VoiceSettings source)
    {
        return new VoiceSettings
        {
            Mode = source.Mode,
            Enabled = source.Enabled,
            SpeechToTextProviderId = source.SpeechToTextProviderId,
            TextToSpeechProviderId = source.TextToSpeechProviderId,
            InputDeviceId = source.InputDeviceId,
            OutputDeviceId = source.OutputDeviceId,
            SampleRateHz = source.SampleRateHz,
            CaptureChunkMs = source.CaptureChunkMs,
            BargeInEnabled = source.BargeInEnabled,
            WakeWord = new VoiceWakeWordSettings
            {
                Engine = source.WakeWord.Engine,
                ModelId = source.WakeWord.ModelId,
                TriggerThreshold = source.WakeWord.TriggerThreshold,
                TriggerCooldownMs = source.WakeWord.TriggerCooldownMs,
                PreRollMs = source.WakeWord.PreRollMs,
                EndSilenceMs = source.WakeWord.EndSilenceMs
            },
            AlwaysOn = new VoiceAlwaysOnSettings
            {
                MinSpeechMs = source.AlwaysOn.MinSpeechMs,
                EndSilenceMs = source.AlwaysOn.EndSilenceMs,
                MaxUtteranceMs = source.AlwaysOn.MaxUtteranceMs,
                AutoSubmit = source.AlwaysOn.AutoSubmit
            }
        };
    }

    private static VoiceStatusInfo Clone(VoiceStatusInfo source)
    {
        return new VoiceStatusInfo
        {
            Available = source.Available,
            Running = source.Running,
            Mode = source.Mode,
            State = source.State,
            SessionKey = source.SessionKey,
            InputDeviceId = source.InputDeviceId,
            OutputDeviceId = source.OutputDeviceId,
            WakeWordModelId = source.WakeWordModelId,
            WakeWordLoaded = source.WakeWordLoaded,
            LastWakeWordUtc = source.LastWakeWordUtc,
            LastUtteranceUtc = source.LastUtteranceUtc,
            LastError = source.LastError
        };
    }

    private static string? BuildProviderFallbackMessage(
        VoiceProviderOption speechToTextProvider,
        VoiceProviderOption textToSpeechProvider)
    {
        var fallbacks = new List<string>();

        if (!VoiceProviderCatalogService.SupportsWindowsRuntime(speechToTextProvider.Id))
        {
            fallbacks.Add($"STT '{speechToTextProvider.Name}' is not implemented yet; using Windows Speech Recognition.");
        }

        if (!VoiceProviderCatalogService.SupportsWindowsRuntime(textToSpeechProvider.Id))
        {
            fallbacks.Add($"TTS '{textToSpeechProvider.Name}' is not implemented yet; using Windows Speech Synthesis.");
        }

        return fallbacks.Count == 0 ? null : string.Join(" ", fallbacks);
    }

    private static string GetUserFacingErrorMessage(Exception ex)
    {
        if (IsSpeechPrivacyDeclined(ex))
        {
            return "Windows online speech recognition is disabled. Open Settings > Privacy & security > Speech and turn on Online speech recognition, then restart Voice Mode.";
        }

        if (ex is UnauthorizedAccessException)
        {
            return "Microphone access is blocked. Open Settings > Privacy & security > Microphone and allow desktop apps to use the microphone.";
        }

        return ex.Message;
    }

    private static bool IsSpeechPrivacyDeclined(Exception ex)
    {
        if (ex.HResult == HResultSpeechPrivacyDeclined)
        {
            return true;
        }

        return ex.Message.Contains("speech privacy policy", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("online speech recognition", StringComparison.OrdinalIgnoreCase);
    }
}
