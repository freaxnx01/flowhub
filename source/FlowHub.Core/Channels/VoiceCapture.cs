namespace FlowHub.Core.Channels;

/// <summary>
/// Shared facts about a voice Capture awaiting transcription. The placeholder is written
/// by the Telegram handler and read by the retry endpoint, which has to recognise a
/// Capture that still has no transcript — so it cannot stay a literal in one module.
/// </summary>
public static class VoiceCapture
{
    /// <summary>Content a voice Capture carries until its transcript replaces it.</summary>
    public const string PlaceholderContent = "[voice message]";
}
