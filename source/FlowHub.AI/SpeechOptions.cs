namespace FlowHub.AI;

/// <summary>
/// Configuration for speech-to-text. Inactive unless <see cref="ApiKey"/> is set, so
/// an unconfigured FlowHub never calls a transcription provider.
/// </summary>
public sealed class SpeechOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Speech";

    /// <summary>Provider API key. Secret — env var only. Absent means the feature is off.</summary>
    public string? ApiKey { get; set; }

    /// <summary>OpenAI-compatible base URL; a cloud provider or a local whisper server.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Transcription model name.</summary>
    public string Model { get; set; } = "whisper-1";

    /// <summary>Per-request HTTP timeout.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Longest audio accepted, in seconds. Checked before download, because
    /// transcription is billed per minute and a mis-sent recording should not
    /// become an unbounded charge.
    /// </summary>
    public int MaxSeconds { get; set; } = 300;

    /// <summary>True when the feature has everything it needs to run.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
