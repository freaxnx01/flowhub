using System.ComponentModel;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

/// <summary>
/// One structured field extracted by the classifier. A closed object in an array, rather
/// than a free-form dictionary: Microsoft.Extensions.AI renders a dictionary as
/// <c>additionalProperties: {"type":"string"}</c>, which Anthropic rejects outright.
/// </summary>
internal sealed record AiEntity(
    [property: Description("The field name, e.g. quote, author, title, year")]
    [property: JsonPropertyName("key")]
    string Key,

    [property: Description("The field value")]
    [property: JsonPropertyName("value")]
    string Value);
