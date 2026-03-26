using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray.Services.Voice;

public sealed class VoiceChatCoordinator : IDisposable
{
    private const int MaxBufferedConversationTurns = 8;
    private readonly IVoiceRuntime _voiceService;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _gate = new();

    private IVoiceChatWindow? _webChatWindow;
    private string _voiceTranscriptDraftText = string.Empty;
    private readonly List<VoiceConversationTurnEventArgs> _bufferedConversationTurns = [];
    private bool _disposed;

    public event EventHandler<VoiceConversationTurnEventArgs>? ConversationTurnAvailable;

    public VoiceChatCoordinator(
        IVoiceRuntime voiceService,
        IUiDispatcher dispatcher)
    {
        _voiceService = voiceService;
        _dispatcher = dispatcher;

        _voiceService.ConversationTurnAvailable += OnVoiceConversationTurnAvailable;
        _voiceService.TranscriptDraftUpdated += OnVoiceTranscriptDraftUpdated;
    }

    public void AttachWindow(IVoiceChatWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            if (ReferenceEquals(_webChatWindow, window))
            {
                return;
            }

            _webChatWindow = window;
        }

        _ = window.UpdateVoiceTranscriptDraftAsync(
            _voiceTranscriptDraftText,
            clear: string.IsNullOrWhiteSpace(_voiceTranscriptDraftText));

        List<VoiceConversationTurnEventArgs> bufferedTurns;
        lock (_gate)
        {
            bufferedTurns = [.. _bufferedConversationTurns];
        }

        foreach (var turn in bufferedTurns)
        {
            _ = window.AppendVoiceConversationTurnAsync(turn);
        }
    }

    public void DetachWindow(IVoiceChatWindow? window)
    {
        lock (_gate)
        {
            if (_webChatWindow == null)
            {
                return;
            }

            if (window != null && !ReferenceEquals(_webChatWindow, window))
            {
                return;
            }

            _webChatWindow = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachWindow(null);
        _voiceService.ConversationTurnAvailable -= OnVoiceConversationTurnAvailable;
        _voiceService.TranscriptDraftUpdated -= OnVoiceTranscriptDraftUpdated;
    }

    private void OnVoiceConversationTurnAvailable(object? sender, VoiceConversationTurnEventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IVoiceChatWindow? window;
            lock (_gate)
            {
                _bufferedConversationTurns.Add(CloneTurn(args));
                if (_bufferedConversationTurns.Count > MaxBufferedConversationTurns)
                {
                    _bufferedConversationTurns.RemoveAt(0);
                }

                window = _webChatWindow;
            }

            if (window != null && !window.IsClosed)
            {
                _ = window.AppendVoiceConversationTurnAsync(args);
            }

            ConversationTurnAvailable?.Invoke(this, args);
        });
    }

    private void OnVoiceTranscriptDraftUpdated(object? sender, VoiceTranscriptDraftEventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _voiceTranscriptDraftText = args.Clear ? string.Empty : (args.Text ?? string.Empty);

            IVoiceChatWindow? window;
            lock (_gate)
            {
                window = _webChatWindow;
            }

            if (window == null || window.IsClosed)
            {
                return;
            }

            _ = window.UpdateVoiceTranscriptDraftAsync(_voiceTranscriptDraftText, args.Clear);
        });
    }

    private static VoiceConversationTurnEventArgs CloneTurn(VoiceConversationTurnEventArgs args)
    {
        return new VoiceConversationTurnEventArgs
        {
            Direction = args.Direction,
            Message = args.Message,
            SessionKey = args.SessionKey
        };
    }
}
