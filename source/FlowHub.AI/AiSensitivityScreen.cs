using System.Diagnostics;
using FlowHub.Core.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FlowHub.AI;

/// <summary>
/// LLM-backed <see cref="ISensitivityScreen"/> (issue #93). Total by construction: every
/// provider, schema, or parse failure degrades to <see cref="Sensitivity.Unsure"/>, which
/// parks the capture. See the remarks on <see cref="ISensitivityScreen"/> for why a throw
/// here would be a disclosure bug rather than an availability one.
/// </summary>
internal sealed partial class AiSensitivityScreen : ISensitivityScreen
{
    private readonly IChatClient _chat;
    private readonly ILogger<AiSensitivityScreen> _log;
    private readonly ChatOptions _options;
    private readonly AiModelInfo _modelInfo;

    public AiSensitivityScreen(
        IChatClient chat,
        ILogger<AiSensitivityScreen> log,
        ChatOptions options,
        AiModelInfo modelInfo)
    {
        _chat = chat;
        _log = log;
        _options = options;
        _modelInfo = modelInfo;
    }

    public async Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await _chat.GetResponseAsync<AiSensitivityResponse>(
                AiPrompts.BuildSensitivityMessages(content),
                _options,
                cancellationToken: cancellationToken);

            sw.Stop();

            if (!response.TryGetResult(out var payload))
            {
                return Park("schema_violation", sw);
            }

            var trace = new ClassifierTrace(
                ClassifierKind.Ai,
                (int)sw.ElapsedMilliseconds,
                _modelInfo.Provider,
                _modelInfo.Model,
                (int?)response.Usage?.InputTokenCount,
                (int?)response.Usage?.OutputTokenCount);

            return payload.Verdict switch
            {
                "safe" => new SensitivityVerdict(Sensitivity.Safe, string.Empty, trace),
                "sensitive" => new SensitivityVerdict(
                    Sensitivity.Sensitive, Reason(payload, "sensitive"), trace),
                "unsure" => new SensitivityVerdict(
                    Sensitivity.Unsure, Reason(payload, "unsure"), trace),
                // AllowedValues constrains the schema, but a provider that ignores it
                // must not be able to produce a routable verdict by accident.
                _ => Park($"unrecognised verdict '{payload.Verdict}'", sw),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a screen failure — the capture is simply not processed yet.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad, against the repo's catch-specific rule. Every escape
            // from this method is a capture that bypasses the privacy guard, so the
            // catch-all IS the feature. Narrowing it would reintroduce issue #93.
            sw.Stop();
            return Park(ex.GetType().Name, sw);
        }
    }

    private static string Reason(AiSensitivityResponse payload, string fallback) =>
        string.IsNullOrWhiteSpace(payload.Reason) ? fallback : payload.Reason;

    private SensitivityVerdict Park(string reason, Stopwatch sw)
    {
        LogScreenDegraded(reason, sw.ElapsedMilliseconds);
        return new SensitivityVerdict(
            Sensitivity.Unsure,
            $"sensitivity screen unavailable — {reason}",
            new ClassifierTrace(
                ClassifierKind.Ai,
                (int)sw.ElapsedMilliseconds,
                _modelInfo.Provider,
                _modelInfo.Model));
    }

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Warning,
        Message = "AiSensitivityScreen degraded to Unsure (reason={Reason}, duration_ms={DurationMs}) — capture parked")]
    private partial void LogScreenDegraded(string reason, long durationMs);
}
