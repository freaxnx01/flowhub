using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

internal sealed record AiClassificationResponse(
    [property: Description("1–5 short lowercase tags describing the snippet")]
    [property: JsonPropertyName("tags")]
    string[] Tags,

    // Microsoft.Extensions.AI generates the structured-output schema from this
    // attribute, so it is what actually constrains the model — the system prompt only
    // advises. "Bridge" must be listed here or the model cannot emit it however the
    // prompt is worded, and it silently answers with the next-best option instead.
    // Kept unconditional rather than gated on Ai:EnableBridgeClassification: the DTO is
    // static, and AiClassifier's own allow-list is what rejects "Bridge" when the flag
    // is off, degrading to the keyword classifier as designed.
    [property: Description("Bridge, Wallabag, Vikunja, or empty string for none")]
    [property: AllowedValues("Bridge", "Wallabag", "Vikunja", "")]
    [property: JsonPropertyName("matched_skill")]
    string MatchedSkill,

    [property: Description("3–8 word title or null if content is too short")]
    [property: JsonPropertyName("title")]
    string? Title,

    [property: Description("Vikunja project bucket name when matched_skill=Vikunja; null otherwise")]
    [property: JsonPropertyName("project")]
    string? Project,

    [property: Description("Optional structured entities the bucket may consume (e.g. quote, author)")]
    [property: JsonPropertyName("entities")]
    Dictionary<string, string>? Entities);
