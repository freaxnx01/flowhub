using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

internal sealed record AiSensitivityResponse(
    [property: Description("sensitive, unsure, or safe")]
    [property: AllowedValues("sensitive", "unsure", "safe")]
    [property: JsonPropertyName("verdict")]
    string Verdict,

    [property: Description("Short reason naming the CATEGORY of sensitivity, never quoting the content")]
    [property: JsonPropertyName("reason")]
    string? Reason);
