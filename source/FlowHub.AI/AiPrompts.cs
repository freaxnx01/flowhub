using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

[assembly: InternalsVisibleTo("FlowHub.Web.ComponentTests")]

namespace FlowHub.AI;

internal static class AiPrompts
{
    internal static string BuildSystemPrompt(
        IReadOnlyCollection<string> vikunjaBuckets, bool allowBridge = false)
    {
        var bucketLine = vikunjaBuckets.Count == 0
            ? "Inbox"
            : string.Join(", ", vikunjaBuckets);

        var bridgeOption = allowBridge
            ? """

                    "Bridge"    – the snippet is an actionable task, bug report, or feature
                                  request about one of the operator's own software projects
            """.TrimEnd('\n')
            : "";

        return string.Create(CultureInfo.InvariantCulture, $$"""
            You classify user-captured snippets for a personal knowledge tool called FlowHub.

            For each capture, return:
            - tags: 1–5 short lowercase tags describing the snippet
            - matched_skill: which downstream skill should handle it. Choose exactly ONE:
                "Wallabag"  – the snippet is a URL or article worth saving for later reading
                "Vikunja"   – the snippet is a task, todo, OR a structured piece of content
                              that belongs in a Vikunja project (quote, movie, book, …){{bridgeOption}}
                ""          – none of the above; it will be marked as Orphan
            - project: when matched_skill="Vikunja", pick the best matching project from
              this list. If unsure, pick "Inbox".
                Available: {{bucketLine}}
              Leave empty otherwise.
            - title: a 3–8 word title summarising the snippet (omit only if the snippet
                     is itself shorter than 8 words)
            - entities: optional structured fields the project may use, e.g.
                Zitate → {"quote": "...", "author": "..."}
                Movies → {"title": "...", "year": "..."}
              Omit if nothing applies.

            Reply ONLY via the structured response schema. Never include explanations.
            """);
    }

    internal static IList<ChatMessage> BuildMessages(
        string content, IReadOnlyCollection<string> vikunjaBuckets, bool allowBridge = false) =>
    [
        new ChatMessage(ChatRole.System, BuildSystemPrompt(vikunjaBuckets, allowBridge)),
        new ChatMessage(ChatRole.User, content),
    ];

    private const string BridgeSystemPrompt = """
        You route a short note to a code repository via the "bridge" tool. Decide whether
        the note should become a GitHub/Forgejo ISSUE or an entry in the repo's ideas.md.

        Return:
        - action: exactly one of
            "issue"   – an actionable bug report, task, or concrete feature request
            "idea"    – a fuzzy, exploratory, or "what if" thought worth keeping
            "unknown" – you genuinely cannot tell; do NOT guess
        - title: a 3–8 word title
        - body: the cleaned-up detail (issue description, or the idea text)
        - tags: 1–3 short lowercase tags

        Reply ONLY via the structured response schema. Never include explanations.
        """;

    internal static IList<ChatMessage> BuildBridgeMessages(string content) =>
    [
        new ChatMessage(ChatRole.System, BridgeSystemPrompt),
        new ChatMessage(ChatRole.User, content),
    ];

    internal static IList<ChatMessage> BuildRepoConfirmMessages(
        string content, IReadOnlyList<(string Name, string? Desc)> candidates)
    {
        var lines = string.Join("\n", candidates.Select(c =>
            c.Desc is null ? $"  - {c.Name}" : $"  - {c.Name} — {c.Desc}"));

        var system = string.Create(CultureInfo.InvariantCulture, $$"""
            You route a developer note to one of the operator's own code repositories.

            Candidate repositories:
            {{lines}}

            Return:
            - repo: the exact name of ONE listed repository, or null if none of them fits.
                    Choosing a repository that does not fit is worse than returning null.
                    Never invent a name that is not in the list above.
            - action: "issue" for an actionable bug report, task, or concrete feature
                      request; "idea" for a fuzzy or exploratory thought
            - title: a 3–8 word title
            - body: the cleaned-up detail

            Reply ONLY via the structured response schema. Never include explanations.
            """);

        return
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, content),
        ];
    }
}
