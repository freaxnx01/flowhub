using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

/// <summary>
/// Structured-output schema for the Bridge action prompt. The model decides issue-vs-idea
/// from wording; "unknown" means it could not confidently choose (do not guess).
/// </summary>
internal sealed record AiBridgeResponse(
    [property: Description("issue = actionable bug/task/feature request; idea = fuzzy or exploratory thought; unknown = genuinely unclear")]
    [property: AllowedValues("issue", "idea", "unknown")]
    [property: JsonPropertyName("action")]
    string Action,

    [property: Description("3–8 word title summarising the item; null if too short")]
    [property: JsonPropertyName("title")]
    string? Title,

    [property: Description("Cleaned-up detail: the issue body, or the idea text")]
    [property: JsonPropertyName("body")]
    string? Body,

    [property: Description("1–3 short lowercase tags")]
    [property: JsonPropertyName("tags")]
    string[]? Tags);
