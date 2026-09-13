using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowHub.Api.Requests;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Api;

public sealed class CapturePreviewEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    // The server writes BridgeAction as a string (JsonStringEnumConverter is configured
    // in FlowHub.Api's ServiceCollectionExtensions). The default System.Text.Json
    // deserializer only accepts numeric enums, so tests must opt in to the same
    // converter to read the response body.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly WebApplicationFactory<Program> _factory;

    public CapturePreviewEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Preview_ReturnsTheClassifiersDecision()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(
                Tags: ["game", "idea"],
                MatchedSkill: "Bridge",
                Title: "Add Giana Sisters Clone",
                BridgeTarget: "freaxnx01/game-n-s-clone",
                BridgeAction: BridgeAction.Issue));

        using var client = CreateClient(classifier);

        var response = await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "Game: Platformer Giana Sisters Clone (Browser)", source = "Web" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CapturePreviewResponse>(JsonOpts);
        body!.MatchedSkill.Should().Be("Bridge");
        body.Title.Should().Be("Add Giana Sisters Clone");
        body.BridgeTarget.Should().Be("freaxnx01/game-n-s-clone");
        body.Tags.Should().BeEquivalentTo(["game", "idea"]);
    }

    [Fact]
    public async Task Preview_PersistsNothing()
    {
        // The criterion the whole feature rests on: a 223-capture survey must leave no trace.
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Inbox"));

        var captures = Substitute.For<ICaptureService>();

        using var client = CreateClient(classifier, captures);

        await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "anything", source = "Web" });

        await captures.DidNotReceive().SubmitAsync(
            Arg.Any<string>(), Arg.Any<ChannelKind>(), Arg.Any<CancellationToken>());
        await captures.DidNotReceive().SubmitAsync(
            Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_InvalidBody_Returns400()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "", source = "Web" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_KnownProject_ResolvesToItsId()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Games Ideen"));

        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Games Ideen"] = 92, ["Inbox"] = 2 });

        using var client = CreateClient(classifier, projectCatalog: catalog);

        var body = await (await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" })).Content.ReadFromJsonAsync<CapturePreviewResponse>(JsonOpts);

        body!.VikunjaProjectId.Should().Be(92);
        body.VikunjaProjectResolved.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_UnknownProject_ReportsUnresolved()
    {
        // The failure this field exists to surface: the classifier names a project that
        // does not exist, and the task would silently land in the fallback instead.
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Does Not Exist"));

        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Inbox"] = 2 });

        using var client = CreateClient(classifier, projectCatalog: catalog);

        var body = await (await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" })).Content.ReadFromJsonAsync<CapturePreviewResponse>(JsonOpts);

        body!.VikunjaProjectId.Should().BeNull();
        body.VikunjaProjectResolved.Should().BeFalse();
        body.VikunjaProject.Should().Be("Does Not Exist");
    }

    [Fact]
    public async Task Preview_CatalogueUnreachable_StillReturns()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(Tags: ["t"], MatchedSkill: "Vikunja", VikunjaProject: "Inbox"));

        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, int>>(_ => throw new HttpRequestException("down"));

        using var client = CreateClient(classifier, projectCatalog: catalog);

        var response = await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CapturePreviewResponse>(JsonOpts);
        body!.VikunjaProjectResolved.Should().BeFalse();
        body.VikunjaProjectId.Should().BeNull();
    }

    [Fact]
    public async Task Preview_NonVikunjaSkill_ReportsNoProject()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(
                Tags: ["t"], MatchedSkill: "Bridge", BridgeTarget: "o/r"));

        using var client = CreateClient(classifier);

        var body = await (await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" })).Content.ReadFromJsonAsync<CapturePreviewResponse>(JsonOpts);

        body!.VikunjaProject.Should().BeNull();
        body.VikunjaProjectId.Should().BeNull();
        body.VikunjaProjectResolved.Should().BeFalse();
    }

    private HttpClient CreateClient(
        IClassifier? classifier = null,
        ICaptureService? captures = null,
        IVikunjaProjectCatalog? projectCatalog = null) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            if (classifier is not null)
            {
                Replace(services, classifier);
            }
            if (captures is not null)
            {
                Replace(services, captures);
            }
            if (projectCatalog is not null)
            {
                Replace(services, projectCatalog);
            }
        })).CreateClient();

    private static void Replace<T>(IServiceCollection services, T impl) where T : class
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(d);
        }
        services.AddSingleton(impl);
    }
}
