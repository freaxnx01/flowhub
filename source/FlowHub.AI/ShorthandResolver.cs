using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FlowHub.Core.Classification;

namespace FlowHub.AI;

/// <param name="Entities">resolved shorthand, merged into ClassificationResult.Entities.</param>
/// <param name="PromptContext">a short block appended to the system prompt; empty when nothing resolved.</param>
/// <param name="UnknownTokens">trailing short tokens that look like shorthand but are not in the glossary.</param>
/// <param name="BridgeTarget">owner/repo when a prefix named one — routes deterministically,
/// skipping repo inference entirely (D5). Null otherwise.</param>
internal sealed record ResolvedShorthand(
    IReadOnlyDictionary<string, string> Entities,
    string PromptContext,
    IReadOnlyList<string> UnknownTokens,
    string? BridgeTarget = null)
{
    public static ResolvedShorthand None { get; } =
        new(new Dictionary<string, string>(), string.Empty, []);
}

/// <summary>
/// Deterministic shorthand resolution. Runs before the model so that resolution is
/// testable without an LLM and the unknown-token rule is enforceable rather than a
/// model judgement. The original text is never rewritten — see D2 in the spec.
/// </summary>
internal static class ShorthandResolver
{
    /// <summary>A trailing alphabetic token of this length or shorter looks like a person marker.</summary>
    private const int ShortTokenMaxLength = 3;

    public static ResolvedShorthand Resolve(string content, GlossarySnapshot glossary)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(glossary);

        if (glossary.IsEmpty || string.IsNullOrWhiteSpace(content))
        {
            return ResolvedShorthand.None;
        }

        var prefix = FindMatchingPrefix(content, glossary);
        if (prefix is not null)
        {
            return ResolveWithPrefix(prefix, glossary);
        }

        var entities = new Dictionary<string, string>(StringComparer.Ordinal);
        var context = new StringBuilder();

        ResolveAcronym(content, glossary, entities, context);
        var matchedPerson = ResolvePerson(content, glossary, entities, context);
        ResolveGreaterThanOperator(content, glossary, entities, context);

        return new ResolvedShorthand(
            entities,
            context.ToString().TrimEnd(),
            FindUnknownTokens(content, glossary, matchedPerson));
    }

    private static string? FindMatchingPrefix(string content, GlossarySnapshot glossary) =>
        glossary.Prefixes.Keys
            .Where(p => content.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();

    private static ResolvedShorthand ResolveWithPrefix(string prefix, GlossarySnapshot glossary)
    {
        // D3: a prefix marker delimits subject matter. Resolve the prefix itself, then
        // stop — everything after it is content, not shorthand.
        var target = glossary.Prefixes[prefix];
        var entities = new Dictionary<string, string>(StringComparer.Ordinal) { ["prefix"] = target };

        // D5: an owner-qualified value names a repo, so the capture routes there
        // deterministically and repo inference is skipped.
        var isRepo = target.Contains('/', StringComparison.Ordinal);
        var context = string.Create(
            CultureInfo.InvariantCulture,
            $"\"{prefix}\" marks this capture as: {target}.");

        return new ResolvedShorthand(entities, context, [], isRepo ? target : null);
    }

    private static void ResolveAcronym(
        string content,
        GlossarySnapshot glossary,
        Dictionary<string, string> entities,
        StringBuilder context)
    {
        foreach (var (token, expansion) in glossary.Acronyms)
        {
            if (MatchesWholeWord(content, token))
            {
                entities["acronym"] = expansion;
                context.Append(CultureInfo.InvariantCulture, $"\"{token}\" means \"{expansion}\". ");
                return;
            }
        }
    }

    private static string? ResolvePerson(
        string content,
        GlossarySnapshot glossary,
        Dictionary<string, string> entities,
        StringBuilder context)
    {
        foreach (var (token, name) in glossary.People)
        {
            if (MatchesWholeWord(content, token))
            {
                entities["person"] = name;
                context.Append(CultureInfo.InvariantCulture, $"\"{token}\" refers to the person {name}. ");
                return token;
            }
        }
        return null;
    }

    private static void ResolveGreaterThanOperator(
        string content,
        GlossarySnapshot glossary,
        Dictionary<string, string> entities,
        StringBuilder context)
    {
        var idx = content.IndexOf('>', StringComparison.Ordinal);
        if (idx < 0)
        {
            return;
        }

        var after = content[(idx + 1)..].Trim();
        var followedByPerson = glossary.People.Keys.Any(t =>
            after.StartsWith(t, StringComparison.OrdinalIgnoreCase)
            && (after.Length == t.Length || !char.IsLetter(after[t.Length])));

        entities["operator"] = followedByPerson ? "recommend-to" : "in-style-of";
        context.Append(followedByPerson
            ? "\">\" before a person means: recommend this to that person. "
            : "\">\" before a thing means: in the style of that thing. ");
    }

    private static IReadOnlyList<string> FindUnknownTokens(
        string content, GlossarySnapshot glossary, string? matchedPerson)
    {
        // Deliberately narrow: only a trailing short alphabetic token. A broader rule
        // parks a large fraction of ordinary captures on common short words.
        var trailing = content.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (trailing is null
            || trailing.Length > ShortTokenMaxLength
            || !trailing.All(char.IsLetter))
        {
            return [];
        }

        if (matchedPerson is not null
            || glossary.People.ContainsKey(trailing)
            || glossary.Acronyms.ContainsKey(trailing))
        {
            return [];
        }

        return [trailing];
    }

    private static bool MatchesWholeWord(string content, string token) =>
        Regex.IsMatch(
            content,
            $@"(?<![\p{{L}}]){Regex.Escape(token)}(?![\p{{L}}])",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
}
