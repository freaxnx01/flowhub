namespace FlowHub.Core.Captures;

/// <summary>
/// Driven port for turning recorded audio into text. Implementations are
/// best-effort: a provider failure returns null rather than throwing, so a
/// transcription problem becomes a lifecycle outcome rather than a pipeline fault.
/// </summary>
public interface ISpeechToText
{
    /// <summary>
    /// Transcribes <paramref name="audio"/>, or returns null when the provider fails
    /// or returns nothing usable. <paramref name="fileName"/> carries the extension
    /// the provider uses to detect the container format.
    /// </summary>
    Task<string?> TranscribeAsync(
        Stream audio, string fileName, CancellationToken cancellationToken = default);
}
