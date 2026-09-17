namespace FlowHub.Core.Captures;

/// <summary>
/// Lifecycle stage of a <see cref="Capture"/>.
/// See Glossary entry "Capture lifecycle" in the CAS Obsidian vault.
/// </summary>
public enum LifecycleStage
{
    /// <summary>Just arrived, no classification yet.</summary>
    Raw,

    /// <summary>AI has assigned a category / target Skill.</summary>
    Classified,

    /// <summary>Handed off to a Skill (in-flight, processing).</summary>
    Routed,

    /// <summary>Skill processed and Integration write succeeded (happy terminal state).</summary>
    Completed,

    /// <summary>Skill or Integration failed during processing.</summary>
    Orphan,

    /// <summary>No matching Skill — triggers a Skill suggestion.</summary>
    Unhandled,

    /// <summary>
    /// Judged sensitive by the pre-pass — withheld from every Integration and never
    /// classified. Terminal and deliberately absent from CaptureRetryEndpoint's
    /// RetryableStages: a retry out of this stage is the irreversible disclosure
    /// this stage exists to prevent (issue #93).
    /// </summary>
    Withheld,
}
