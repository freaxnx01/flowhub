using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Web.Pipeline;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureEnrichmentConsumerTests
{
    [Fact]
    public async Task Consume_UrlContent_PublishesCaptureClassifiedAndMarksClassified()
    {
        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(StubClassifier(new ClassificationResult(["link"], "Wallabag"))),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("https://example.com", ChannelKind.Web, default);

        (await harness.Consumed.Any<CaptureCreated>(
            x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        (await harness.Published.Any<CaptureClassified>(
            x => x.Context.Message.CaptureId == capture.Id
                && x.Context.Message.MatchedSkill == "Wallabag"))
            .Should().BeTrue();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().Be(LifecycleStage.Classified);
        stored.MatchedSkill.Should().Be("Wallabag");
    }

    [Fact]
    public async Task Consume_EmptyClassification_MarksOrphanWithoutPublishingClassified()
    {
        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(StubClassifier(new ClassificationResult(["unsorted"], string.Empty))),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("plain text", ChannelKind.Web, default);

        (await harness.Consumed.Any<CaptureCreated>(
            x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        (await harness.Published.Any<CaptureClassified>())
            .Should().BeFalse();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().Be(LifecycleStage.Orphan);
    }

    [Fact]
    public async Task Consume_KnownSkillWithTitle_PassesTitleToCaptureService()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(["link"], "Wallabag", Title: "Hexagonal architecture"));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("https://example.com", ChannelKind.Web, default);

        (await harness.Consumed.Any<CaptureCreated>(
            x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        var updated = await captureService.GetByIdAsync(capture.Id, default);
        updated!.Title.Should().Be("Hexagonal architecture");
    }

    [Fact]
    public async Task Consume_AttachmentCapture_RoutesToPaperless_WithoutCallingClassifier()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ClassificationResult>>(_ => throw new InvalidOperationException("classifier must not be called for attachments"));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
        var capture = await captureService.SubmitAsync(
            caption: null, ChannelKind.Web,
            new AttachmentInput { Content = bytes, FileName = "scan.pdf", ContentType = "application/pdf", SizeBytes = 3 },
            cancellationToken: default);

        (await harness.Published.Any<CaptureClassified>(
            x => x.Context.Message.CaptureId == capture.Id
                && x.Context.Message.MatchedSkill == "Paperless"))
            .Should().BeTrue();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.MatchedSkill.Should().Be("Paperless");
    }

    [Theory]
    [InlineData("Tschau Sepp Bug Report:")]
    [InlineData("Einkaufsliste:")]
    [InlineData("Notizen :")]
    public async Task Consume_HeaderWithNoBody_MarksUnhandledWithoutClassifying(string content)
    {
        // A capture whose whole content is a colon-terminated header announces a body
        // that is not there — typically Enter sent the message while the operator was
        // reaching for a newline. Capture bd3ee30d ("Tschau Sepp Bug Report:") became
        // game-tschau-sepp#31: a real issue, empty body, nobody wrote it on purpose.
        var classifier = Substitute.For<IClassifier>();
        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync(content, ChannelKind.Telegram, default);

        (await harness.Consumed.Any<CaptureCreated>(
            x => x.Context.Message.CaptureId == capture.Id)).Should().BeTrue();

        // No classification at all: no LLM call, and nothing published downstream.
        await classifier.DidNotReceiveWithAnyArgs().ClassifyAsync(default!, default);
        (await harness.Published.Any<CaptureClassified>()).Should().BeFalse();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().Be(LifecycleStage.Unhandled);
    }

    [Theory]
    [InlineData("Baldrian")]
    [InlineData("23 x 23 x 13")]
    [InlineData("Tschau Sepp Bug Report: Spiel endet nicht")]
    public async Task Consume_ShortButSubstantiveContent_IsStillClassified(string content)
    {
        // The guard is about an announced-but-absent body, not about length. These are
        // all short and all real captures from CT 136 — they must still be classified.
        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(StubClassifier(new ClassificationResult(["task"], "Vikunja"))),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync(content, ChannelKind.Telegram, default);

        (await harness.Published.Any<CaptureClassified>(
            x => x.Context.Message.CaptureId == capture.Id)).Should().BeTrue();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.MatchedSkill.Should().Be("Vikunja");
    }

    [Fact]
    public async Task Consume_NeedsTranscription_DoesNotClassifyOrPaperlessRoute()
    {
        var classifier = Substitute.For<IClassifier>();
        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CaptureCreated(
            Guid.NewGuid(), "[voice message]", ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: true, NeedsTranscription: true));

        (await harness.Consumed.Any<CaptureCreated>()).Should().BeTrue();
        // Neither path may run: not classification, and not the Paperless shortcut
        // that any attachment would otherwise take.
        await classifier.DidNotReceiveWithAnyArgs().ClassifyAsync(default!, default);
        (await harness.Published.Any<CaptureClassified>()).Should().BeFalse();
    }

    private static IClassifier StubClassifier(ClassificationResult result)
    {
        var sub = Substitute.For<IClassifier>();
        sub.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return sub;
    }
}
