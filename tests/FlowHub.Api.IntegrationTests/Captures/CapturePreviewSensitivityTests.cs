using FlowHub.Api.Endpoints;
using FlowHub.Api.Requests;
using FlowHub.Api.Validation;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Api.IntegrationTests.Captures;

public sealed class CapturePreviewSensitivityTests
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

    private static ISensitivityScreen ScreenReturning(Sensitivity verdict, string reason = "")
    {
        var screen = Substitute.For<ISensitivityScreen>();
        screen.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new SensitivityVerdict(verdict, reason));
        return screen;
    }

    [Fact]
    public async Task Preview_SensitiveVerdict_ReturnsVerdictAndProposesNoTarget()
    {
        var screen = ScreenReturning(Sensitivity.Sensitive, "health detail about a named person");
        var result = await CapturePreviewEndpoint.PreviewAsync(
            new CreateCaptureRequest("anything", ChannelKind.Web),
            new CreateCaptureRequestValidator(),
            screen,
            ClassifierReturning(new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja")),
            CatalogOf(("Inbox", 2)),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var body = result.Result.Should().BeOfType<Ok<CapturePreviewResponse>>().Subject.Value!;

        body.Sensitivity.Should().Be(Sensitivity.Sensitive);
        body.SensitivityReason.Should().Be("health detail about a named person");

        // No target is proposed — preview mirrors the production path, and a proposed
        // target for a capture that will never be routed invites someone to act on it.
        body.MatchedSkill.Should().BeEmpty();
        body.VikunjaProject.Should().BeNull();
        body.VikunjaProjectId.Should().BeNull();
    }

    [Fact]
    public async Task Preview_SafeVerdict_ReturnsTheUsualShape()
    {
        var screen = ScreenReturning(Sensitivity.Safe, string.Empty);
        var result = await CapturePreviewEndpoint.PreviewAsync(
            new CreateCaptureRequest("https://example.com/article", ChannelKind.Web),
            new CreateCaptureRequestValidator(),
            screen,
            ClassifierReturning(new ClassificationResult(Tags: ["link"], MatchedSkill: "Wallabag")),
            CatalogOf(("Inbox", 2)),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var body = result.Result.Should().BeOfType<Ok<CapturePreviewResponse>>().Subject.Value!;

        body.Sensitivity.Should().Be(Sensitivity.Safe);
        body.SensitivityReason.Should().BeNull();
    }
}
