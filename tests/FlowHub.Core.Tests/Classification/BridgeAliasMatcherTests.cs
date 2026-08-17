using FlowHub.Core.Classification;
using FluentAssertions;

namespace FlowHub.Core.Tests.Classification;

public sealed class BridgeAliasMatcherTests
{
    private static readonly IReadOnlySet<string> Aliases =
        new HashSet<string>(StringComparer.Ordinal) { "br", "agp", "ainstr" };

    [Fact]
    public void TryMatch_LeadingAliasWithBody_ReturnsAliasAndRemainder()
    {
        var matched = BridgeAliasMatcher.TryMatch("br the login 500s on Safari", Aliases, out var alias, out var remainder);

        matched.Should().BeTrue();
        alias.Should().Be("br");
        remainder.Should().Be("the login 500s on Safari");
    }

    [Fact]
    public void TryMatch_UppercaseAlias_MatchesCaseInsensitively()
    {
        var matched = BridgeAliasMatcher.TryMatch("BR fix the thing", Aliases, out var alias, out var remainder);

        matched.Should().BeTrue();
        alias.Should().Be("br");
        remainder.Should().Be("fix the thing");
    }

    [Fact]
    public void TryMatch_LeadingWhitespaceAndExtraSpaces_TrimsBoth()
    {
        var matched = BridgeAliasMatcher.TryMatch("   agp    do the thing  ", Aliases, out var alias, out var remainder);

        matched.Should().BeTrue();
        alias.Should().Be("agp");
        remainder.Should().Be("do the thing");
    }

    [Fact]
    public void TryMatch_AliasIsPrefixOfLongerToken_DoesNotMatch()
    {
        var matched = BridgeAliasMatcher.TryMatch("brxyz something", Aliases, out _, out _);

        matched.Should().BeFalse();
    }

    [Fact]
    public void TryMatch_AliasWithNoBody_DoesNotMatch()
    {
        var matched = BridgeAliasMatcher.TryMatch("br", Aliases, out _, out _);

        matched.Should().BeFalse();
    }

    [Fact]
    public void TryMatch_NonAliasLeadingToken_DoesNotMatch()
    {
        var matched = BridgeAliasMatcher.TryMatch("hello world", Aliases, out _, out _);

        matched.Should().BeFalse();
    }

    [Fact]
    public void TryMatch_EmptyAliasSet_DoesNotMatch()
    {
        var matched = BridgeAliasMatcher.TryMatch("br the login 500s", new HashSet<string>(), out _, out _);

        matched.Should().BeFalse();
    }

    [Fact]
    public void TryMatch_NullOrWhitespaceContent_DoesNotMatch()
    {
        BridgeAliasMatcher.TryMatch("", Aliases, out _, out _).Should().BeFalse();
        BridgeAliasMatcher.TryMatch("   ", Aliases, out _, out _).Should().BeFalse();
    }
}
