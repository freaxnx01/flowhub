using FlowHub.AI;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class ShorthandResolverTests
{
    private static readonly GlossarySnapshot Glossary = new(
        People: new Dictionary<string, string>
        {
            ["aa"] = "Person A",
            ["a"] = "Person A",
            ["b"] = "Person B",
        },
        Acronyms: new Dictionary<string, string> { ["ZZZ"] = "Zed Zed Zed" },
        Prefixes: new Dictionary<string, string>
        {
            ["Thing:"] = "things",
            ["Repo Thing:"] = "someone/some-repo",
        });

    [Theory]
    [InlineData("Farmshop aa")]
    [InlineData("aa reisen radio")]
    [InlineData("Packlist aa: radio")]
    public void Resolve_PersonTokenInAnyPosition_IsResolved(string content)
    {
        var result = ShorthandResolver.Resolve(content, Glossary);

        result.Entities.Should().ContainKey("person").WhoseValue.Should().Be("Person A");
    }

    [Fact]
    public void Resolve_PersonTokenAfterAMarkerPreposition_IsResolved()
    {
        ShorthandResolver.Resolve("Coffee flavoured für aa", Glossary)
            .Entities.Should().ContainKey("person").WhoseValue.Should().Be("Person A");
    }

    [Fact]
    public void Resolve_AliasTokens_ResolveToTheSamePerson()
    {
        ShorthandResolver.Resolve("Coffee für a", Glossary)
            .Entities["person"].Should().Be("Person A");
        ShorthandResolver.Resolve("Coffee für aa", Glossary)
            .Entities["person"].Should().Be("Person A");
    }

    [Fact]
    public void Resolve_OwnerQualifiedPrefix_YieldsABridgeTarget()
    {
        var result = ShorthandResolver.Resolve("Repo Thing: SPQR", Glossary);

        result.BridgeTarget.Should().Be("someone/some-repo");
        result.Entities.Should().NotContainKey("acronym");
    }

    [Fact]
    public void Resolve_UnqualifiedPrefix_YieldsNoBridgeTarget()
    {
        ShorthandResolver.Resolve("Thing: ZZZ", Glossary).BridgeTarget.Should().BeNull();
    }

    [Fact]
    public void Resolve_PersonTokenIsCaseInsensitive()
    {
        ShorthandResolver.Resolve("Packlist B: radio", Glossary)
            .Entities.Should().ContainKey("person").WhoseValue.Should().Be("Person B");
    }

    [Fact]
    public void Resolve_TokenInsideAWord_IsNotResolved()
    {
        ShorthandResolver.Resolve("Aabach wanderung", Glossary)
            .Entities.Should().NotContainKey("person");
    }

    [Fact]
    public void Resolve_KnownAcronym_IsExpanded()
    {
        ShorthandResolver.Resolve("ZZZ 1800", Glossary)
            .Entities.Should().ContainKey("acronym").WhoseValue.Should().Be("Zed Zed Zed");
    }

    [Fact]
    public void Resolve_GreaterThanBeforeAPerson_MeansRecommendTo()
    {
        ShorthandResolver.Resolve("ZZZ 1800 > aa", Glossary)
            .Entities.Should().ContainKey("operator").WhoseValue.Should().Be("recommend-to");
    }

    [Fact]
    public void Resolve_GreaterThanBeforeAThing_EmitsNoOperatorEntity()
    {
        // Deliberate narrowing after review: "in the style of" is indistinguishable from
        // an ordinary comparison, and the captures that actually mean it carry a prefix
        // marker, which short-circuits before operator resolution. Emitting nothing beats
        // emitting a guess that skews the prompt.
        ShorthandResolver.Resolve("Game: buggy > micro machines", Glossary)
            .Entities.Should().NotContainKey("operator");
    }

    [Fact]
    public void Resolve_KnownPrefix_IsResolvedAsRoutingHint()
    {
        ShorthandResolver.Resolve("Thing: ZZZ", Glossary)
            .Entities.Should().ContainKey("prefix").WhoseValue.Should().Be("things");
    }

    [Fact]
    public void Resolve_AcronymAfterAPrefix_IsSubjectMatterAndIsNotExpanded()
    {
        var result = ShorthandResolver.Resolve("Thing: ZZZ", Glossary);

        result.Entities.Should().ContainKey("prefix");
        result.Entities.Should().NotContainKey("acronym");
    }

    [Fact]
    public void Resolve_UnknownTrailingShortToken_IsReported()
    {
        ShorthandResolver.Resolve("Farmshop qq", Glossary)
            .UnknownTokens.Should().ContainSingle().Which.Should().Be("qq");
    }

    [Fact]
    public void Resolve_TrailingWordLongerThanThreeChars_IsNotReported()
    {
        ShorthandResolver.Resolve("Acronym Quiz: SPQR", Glossary)
            .UnknownTokens.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_TrailingKnownToken_IsNotReportedAsUnknown()
    {
        ShorthandResolver.Resolve("Farmshop aa", Glossary)
            .UnknownTokens.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EmptyGlossary_ResolvesNothingAndReportsNoUnknowns()
    {
        var result = ShorthandResolver.Resolve("Farmshop qq", GlossarySnapshot.Empty);

        result.Entities.Should().BeEmpty();
        result.PromptContext.Should().BeEmpty();
        result.UnknownTokens.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_PromptContext_NamesEveryResolvedToken()
    {
        var context = ShorthandResolver.Resolve("ZZZ 1800 > aa", Glossary).PromptContext;

        context.Should().Contain("ZZZ").And.Contain("Zed Zed Zed");
        context.Should().Contain("aa").And.Contain("Person A");
    }

    [Theory]
    [InlineData("refactor a->b in the parser")]      // arrow
    [InlineData("if (a > b) return;")]                // comparison inside code
    [InlineData("map x => x.Name")]                   // lambda
    [InlineData("compare 5 >= 3")]                    // relational
    public void Resolve_GreaterThanThatIsNotAnOperator_DoesNotEmitAnOperatorEntity(string content)
    {
        // A bare ">" appears in code snippets and arrows all over the corpus. Emitting an
        // operator entity for those injects misleading context into the prompt.
        ShorthandResolver.Resolve(content, Glossary)
            .Entities.Should().NotContainKey("operator");
    }

    [Fact]
    public void Resolve_WhitespaceDelimitedGreaterThan_IsStillAnOperator()
    {
        ShorthandResolver.Resolve("ZZZ 1800 > aa", Glossary)
            .Entities.Should().ContainKey("operator").WhoseValue.Should().Be("recommend-to");
    }
}
