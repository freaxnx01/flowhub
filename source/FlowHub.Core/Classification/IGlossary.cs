namespace FlowHub.Core.Classification;

/// <summary>
/// The operator's private shorthand. Personal data — supplied by the deployment,
/// never committed. An unconfigured deployment gets <see cref="GlossarySnapshot.Empty"/>,
/// which must leave classification byte-identical to having no glossary at all.
/// </summary>
public interface IGlossary
{
    Task<GlossarySnapshot> GetAsync(CancellationToken cancellationToken);
}

/// <param name="People">token → display name, e.g. a one-to-three character marker.</param>
/// <param name="Acronyms">token → expansion.</param>
/// <param name="Prefixes">prefix marker (including its punctuation) → routing hint.
/// Text after a prefix is subject matter and is not resolved — see D3 in the spec.</param>
public sealed record GlossarySnapshot(
    IReadOnlyDictionary<string, string> People,
    IReadOnlyDictionary<string, string> Acronyms,
    IReadOnlyDictionary<string, string> Prefixes)
{
    public static GlossarySnapshot Empty { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>());

    public bool IsEmpty => People.Count == 0 && Acronyms.Count == 0 && Prefixes.Count == 0;
}
