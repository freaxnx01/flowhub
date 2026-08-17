using FlowHub.Core.Skills;

namespace FlowHub.Core.Classification;

/// <summary>
/// Deterministic keyword-based classifier (Block 3 Slice B), also the AI classifier's
/// error fallback. Detects a leading repo-alias token (→ Bridge, action left Unknown for
/// triage) before the url/todo rules.
/// </summary>
public sealed class KeywordClassifier : IClassifier
{
    private readonly IBridgeCatalog _bridgeCatalog;

    public KeywordClassifier(IBridgeCatalog bridgeCatalog)
    {
        _bridgeCatalog = bridgeCatalog;
    }

    public async Task<ClassificationResult> ClassifyAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var aliases = await _bridgeCatalog.GetAliasesAsync(cancellationToken);

        ClassificationResult result;
        if (BridgeAliasMatcher.TryMatch(content, aliases, out var alias, out _))
        {
            // Deterministic path detects the alias but cannot infer issue-vs-idea; leave
            // BridgeAction=Unknown so the pipeline parks it for triage.
            result = new ClassificationResult(["bridge"], "Bridge", BridgeAlias: alias);
        }
        else
        {
            result =
                LooksLikeUrl(content) ? new ClassificationResult(["link"], "Wallabag")
                : ContainsTodoKeyword(content) ? new ClassificationResult(["task"], "Vikunja")
                : new ClassificationResult(["unsorted"], string.Empty);
        }

        sw.Stop();
        return result with
        {
            Trace = new ClassifierTrace(ClassifierKind.Keyword, (int)sw.ElapsedMilliseconds),
        };
    }

    private static bool LooksLikeUrl(string content) =>
        Uri.TryCreate(content.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool ContainsTodoKeyword(string content) =>
        content.Contains("todo", StringComparison.OrdinalIgnoreCase)
        || content.Contains("task", StringComparison.OrdinalIgnoreCase);
}
