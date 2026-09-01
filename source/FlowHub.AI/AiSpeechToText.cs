using FlowHub.Core.Captures;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;

namespace FlowHub.AI;

/// <summary>
/// <see cref="ISpeechToText"/> over the OpenAI-compatible /v1/audio/transcriptions
/// endpoint. The provider is chosen by the client's configured BaseUrl, so the same
/// adapter serves a cloud provider or a local whisper server — see the design's D1.
/// Provider failures return null; the caller decides what that means for the Capture.
/// </summary>
public sealed partial class AiSpeechToText : ISpeechToText
{
    private readonly AudioClient _client;
    private readonly ILogger<AiSpeechToText> _log;

    public AiSpeechToText(AudioClient client, ILogger<AiSpeechToText> log)
    {
        _client = client;
        _log = log;
    }

    public async Task<string?> TranscribeAsync(
        Stream audio, string fileName, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.TranscribeAudioAsync(
                audio, fileName, new AudioTranscriptionOptions(), cancellationToken);
            var text = result.Value?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        // A TaskCanceledException with the token actually cancelled is caller intent —
        // pipeline shutdown or a per-message timeout — and must propagate so it surfaces
        // as a retryable fault rather than a terminal Orphan. The same exception with a
        // live token is an HTTP timeout, which is an ordinary transcription failure.
        // Matches RepoResolver.cs:97 and Enrichers/ZitateEnricher.cs:69-72.
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            LogTranscriptionFailed(ex, fileName);
            return null;
        }
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning,
        Message = "Transcription failed (fileName={FileName})")]
    private partial void LogTranscriptionFailed(Exception ex, string fileName);
}
