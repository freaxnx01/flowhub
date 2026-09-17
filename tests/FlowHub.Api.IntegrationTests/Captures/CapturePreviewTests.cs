using FlowHub.Api.Endpoints;
using FlowHub.Api.Requests;
using FlowHub.Api.Validation;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Api.IntegrationTests.Captures;

/// <summary>
/// The preview endpoint answers "where would this capture go" without writing
/// anything. These call the handler directly, like RetryRepublishTests — which is
/// also what gets FlowHub.Api instrumented for the coverage gate; endpoint tests
/// living in FlowHub.Web.ComponentTests leave the assembly with no coverage data
/// on the CI runner (see the note in coverage.thresholds.json).
/// </summary>
public sealed class CapturePreviewTests
{
    private static IClassifier ClassifierReturning(ClassificationResult result)
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return classifier;
    }

    private static IVikunjaProjectCatalog CatalogOf(params (string Name, int Id)[] entries)
    {
        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns(entries.ToDictionary(e => e.Name, e => e.Id, StringComparer.Ordinal));
        return catalog;
    }

    private static ISensitivityScreen ScreenReturning(Sensitivity verdict)
    {
        var screen = Substitute.For<ISensitivityScreen>();
        screen.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SensitivityVerdict(verdict, string.Empty));
        return screen;
    }

    private static async Task<CapturePreviewResponse> PreviewAsync(
        ClassificationResult classification,
        IVikunjaProjectCatalog? catalog = null,
        ISensitivityScreen? screen = null,
        string content = "anything")
    {
        var result = await CapturePreviewEndpoint.PreviewAsync(
            new CreateCaptureRequest(content, ChannelKind.Web),
            new CreateCaptureRequestValidator(),
            screen ?? ScreenReturning(Sensitivity.Safe),
            ClassifierReturning(classification),
            catalog ?? CatalogOf(("Inbox", 2)),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        return result.Result.Should().BeOfType<Ok<CapturePreviewResponse>>().Subject.Value!;
    }

    [Fact]
    public async Task Preview_ReturnsTheClassifiersDecision()
    {
        var body = await PreviewAsync(new ClassificationResult(
            Tags: ["game", "idea"], MatchedSkill: "Bridge", Title: "Add Giana Sisters Clone",
            BridgeTarget: "freaxnx01/game-n-s-clone", BridgeAction: BridgeAction.Issue));

        body.MatchedSkill.Should().Be("Bridge");
        body.Title.Should().Be("Add Giana Sisters Clone");
        body.BridgeTarget.Should().Be("freaxnx01/game-n-s-clone");
        body.BridgeAction.Should().Be(BridgeAction.Issue);
        body.Tags.Should().BeEquivalentTo(["game", "idea"]);
    }

    [Fact]
    public async Task Preview_KnownProject_ResolvesToItsId()
    {
        var body = await PreviewAsync(
            new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Games Ideen"),
            CatalogOf(("Games Ideen", 92), ("Inbox", 2)));

        body.VikunjaProjectId.Should().Be(92);
        body.VikunjaProjectResolved.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_UnknownProject_ReportsUnresolvedButKeepsTheName()
    {
        // The failure this field exists to surface: the classifier names a project that
        // does not exist, and the task would silently land in the fallback instead.
        var body = await PreviewAsync(
            new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Does Not Exist"),
            CatalogOf(("Inbox", 2)));

        body.VikunjaProjectId.Should().BeNull();
        body.VikunjaProjectResolved.Should().BeFalse();
        body.VikunjaProject.Should().Be("Does Not Exist");
    }

    [Fact]
    public async Task Preview_CatalogueUnreachable_StillReturns()
    {
        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, int>>(_ => throw new HttpRequestException("down"));

        var body = await PreviewAsync(
            new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Inbox"),
            catalog);

        body.VikunjaProjectResolved.Should().BeFalse();
        body.VikunjaProjectId.Should().BeNull();
    }

    [Fact]
    public async Task Preview_UnexpectedCatalogueFailure_IsNotSwallowed()
    {
        // Only an unreachable catalogue is tolerated. A genuine bug in resolution must
        // surface rather than be reported as "unresolved" with no diagnostic trail.
        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, int>>(_ => throw new InvalidOperationException("bug"));

        var act = () => PreviewAsync(
            new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Inbox"),
            catalog);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Preview_NonVikunjaSkill_ReportsNoProject()
    {
        var body = await PreviewAsync(
            new ClassificationResult(Tags: ["t"], MatchedSkill: "Bridge", BridgeTarget: "o/r"));

        body.VikunjaProject.Should().BeNull();
        body.VikunjaProjectId.Should().BeNull();
        body.VikunjaProjectResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Preview_InvalidBody_ReturnsValidationProblem()
    {
        var result = await CapturePreviewEndpoint.PreviewAsync(
            new CreateCaptureRequest(string.Empty, ChannelKind.Web),
            new CreateCaptureRequestValidator(),
            ScreenReturning(Sensitivity.Safe),
            ClassifierReturning(new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja")),
            CatalogOf(("Inbox", 2)),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        result.Result.Should().BeOfType<ValidationProblem>();
    }
}
