namespace FlowHub.Core.Captures;

/// <summary>
/// Aggregated counts of captures needing operator attention.
/// Drives the Dashboard's "Needs Attention" widget.
/// </summary>
/// <param name="OrphanCount">Captures no skill matched.</param>
/// <param name="UnhandledCount">Captures whose routing failed.</param>
/// <param name="WithheldCount">
/// Captures parked by the sensitivity screen. Defaulted so existing call sites keep
/// compiling; it is terminal and non-retryable, so it still needs the operator's eyes.
/// </param>
public sealed record FailureCounts(int OrphanCount, int UnhandledCount, int WithheldCount = 0)
{
    public bool AnyFailures => OrphanCount > 0 || UnhandledCount > 0 || WithheldCount > 0;
}
