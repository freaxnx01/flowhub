using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using FlowHub.AI;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassificationResponseTests
{
    [Fact]
    public void Tags_HasDescription_ForSchemaGeneration()
    {
        var prop = typeof(AiClassificationResponse).GetProperty(nameof(AiClassificationResponse.Tags))!;
        var desc = prop.GetCustomAttribute<DescriptionAttribute>();

        desc.Should().NotBeNull();
        desc!.Description.Should().Contain("tags");
    }

    [Fact]
    public void MatchedSkill_AllowedValuesEnumeratesEverySkillTheModelMayReturn()
    {
        // This attribute is what Microsoft.Extensions.AI turns into the structured-output
        // schema, so it is the real constraint on the model — the system prompt only
        // advises. A skill missing here cannot be returned however the prompt is worded,
        // and the model silently answers with the next-best option instead. #37 added
        // "Bridge" to the prompt and to AiClassifier's allow-list but not here, so on the
        // 0.4.0 deployment every dev capture came back "Vikunja" with nothing to notice.
        var prop = typeof(AiClassificationResponse).GetProperty(nameof(AiClassificationResponse.MatchedSkill))!;
        var allowed = prop.GetCustomAttribute<AllowedValuesAttribute>();

        allowed.Should().NotBeNull();
        allowed!.Values.Should().BeEquivalentTo(new object[] { "Bridge", "Wallabag", "Vikunja", "" });
    }

    [Fact]
    public void Title_IsNullableString()
    {
        var prop = typeof(AiClassificationResponse).GetProperty(nameof(AiClassificationResponse.Title))!;

        prop.PropertyType.Should().Be<string>();
        // Reading nullability via reflection in C# 10+ requires NullabilityInfoContext.
        var ctx = new NullabilityInfoContext();
        ctx.Create(prop).WriteState.Should().Be(NullabilityState.Nullable);
    }
}
