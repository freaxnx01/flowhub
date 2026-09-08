namespace FlowHub.AI;

/// <summary>Bound from configuration section <c>Ai:Glossary</c>.</summary>
public sealed class GlossaryOptions
{
    public const string SectionName = "Ai:Glossary";

    /// <summary>Absolute path to the glossary JSON. Null or empty disables the glossary.</summary>
    public string? Path { get; set; }

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
}
