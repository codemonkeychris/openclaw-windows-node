namespace OpenClawTray.Windows;

internal sealed class WebChatVoiceDomState
{
    public WebChatVoiceDomState(bool stripInjectedMemories)
    {
        StripInjectedMemories = stripInjectedMemories;
    }

    public bool StripInjectedMemories { get; private set; }

    public string PendingDraft { get; private set; } = string.Empty;

    public void SetDraft(string? text, bool clear)
    {
        PendingDraft = clear ? string.Empty : (text ?? string.Empty);
    }

    public void SetStripInjectedMemories(bool enabled)
    {
        StripInjectedMemories = enabled;
    }
}
