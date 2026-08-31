using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

internal sealed record AiRepoConfirmResponse(
    [property: Description("Exact name of one listed repository, or null if none fits")]
    [property: JsonPropertyName("repo")]
    string? Repo,

    [property: Description("issue for an actionable bug or feature request; idea for an exploratory thought")]
    [property: AllowedValues("issue", "idea")]
    [property: JsonPropertyName("action")]
    string Action,

    [property: Description("3–8 word title")]
    [property: JsonPropertyName("title")]
    string? Title,

    [property: Description("Cleaned-up detail: the issue description, or the idea text")]
    [property: JsonPropertyName("body")]
    string? Body);
