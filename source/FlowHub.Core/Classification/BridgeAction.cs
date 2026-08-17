namespace FlowHub.Core.Classification;

/// <summary>
/// The action a Bridge-routed capture resolves to. <see cref="Unknown"/> (the default)
/// means the classifier could not confidently pick issue-vs-idea; the pipeline parks such
/// captures as Unhandled for manual triage rather than guessing.
/// </summary>
public enum BridgeAction
{
    /// <summary>Could not determine the action — leave for triage.</summary>
    Unknown = 0,

    /// <summary>Create a new issue on the target repo.</summary>
    Issue,

    /// <summary>Append an entry to the target repo's <c>ideas.md</c>.</summary>
    Idea,
}
