using System.Net;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;

namespace FlowHub.Skills.Tests.Bridge;

public sealed class BridgeSkillIntegrationTests
{
    private const string BaseUrl = "https://bridge.example.com";
    private const string Token = "bridge-token";

    private static (BridgeSkillIntegration sut, MockHttpMessageHandler mock) Build()
    {
        var mock = new MockHttpMessageHandler();
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri(BaseUrl);
        var options = Options.Create(new BridgeOptions { BaseUrl = BaseUrl, ApiToken = Token });
        return (new BridgeSkillIntegration(http, options, NullLogger<BridgeSkillIntegration>.Instance), mock);
    }

    private static Capture BridgeCapture(BridgeAction action, string alias = "br",
        string? title = "Login 500", string? body = "the login 500s") =>
        new(Guid.NewGuid(), ChannelKind.Web, $"{alias} {body}", DateTimeOffset.UtcNow,
            LifecycleStage.Routed, "Bridge", Title: title, BridgeAlias: alias, BridgeAction: action, BridgeBody: body);

    [Fact]
    public void Name_IsBridge()
    {
        var (sut, _) = Build();
        sut.Name.Should().Be("Bridge");
    }

    [Fact]
    public async Task HandleAsync_Issue_PostsToIssueEndpointAndReturnsUrl()
    {
        var (sut, mock) = Build();
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/issue")
            .WithHeaders("Authorization", $"Bearer {Token}")
            .WithPartialContent("\"alias\":\"br\"")
            .WithPartialContent("\"title\":\"Login 500\"")
            .WithPartialContent("\"body\":\"the login 500s\"")
            .Respond("application/json", """{"url":"https://forge/issues/42","number":42}""");

        var result = await sut.HandleAsync(BridgeCapture(BridgeAction.Issue), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().Be("https://forge/issues/42");
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_Idea_PostsToIdeaEndpointWithText()
    {
        var (sut, mock) = Build();
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/idea")
            .WithHeaders("Authorization", $"Bearer {Token}")
            .WithPartialContent("\"alias\":\"agp\"")
            .WithPartialContent("\"text\":\"what if repos had a health score\"")
            .Respond("application/json", """{"url":"https://forge/ideas.md#abc"}""");

        var capture = BridgeCapture(BridgeAction.Idea, alias: "agp", title: null, body: "what if repos had a health score");
        var result = await sut.HandleAsync(capture, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().Be("https://forge/ideas.md#abc");
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_MissingAlias_ThrowsBeforeCallingServer()
    {
        var (sut, mock) = Build();
        var capture = BridgeCapture(BridgeAction.Issue) with { BridgeAlias = null };

        var act = () => sut.HandleAsync(capture, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        mock.GetMatchCount(mock.When("*")).Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_UnknownAction_ThrowsBeforeCallingServer()
    {
        var (sut, mock) = Build();

        var act = () => sut.HandleAsync(BridgeCapture(BridgeAction.Unknown), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        mock.GetMatchCount(mock.When("*")).Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_ServerReturns401_ThrowsHttpRequestException()
    {
        var (sut, mock) = Build();
        mock.When(HttpMethod.Post, $"{BaseUrl}/api/capture/issue").Respond(HttpStatusCode.Unauthorized);

        var act = () => sut.HandleAsync(BridgeCapture(BridgeAction.Issue), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task HandleAsync_ResponseMissingUrl_ThrowsInvalidOperation()
    {
        var (sut, mock) = Build();
        mock.When(HttpMethod.Post, $"{BaseUrl}/api/capture/issue")
            .Respond("application/json", """{"number":7}""");

        var act = () => sut.HandleAsync(BridgeCapture(BridgeAction.Issue), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task HandleAsync_IssueWithEmptyTitle_FallsBackToBridgeBodyAsTitle()
    {
        var (sut, mock) = Build();
        var capture = BridgeCapture(BridgeAction.Issue, title: null, body: "fix the flaky login test");
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/issue")
            .WithPartialContent("\"title\":\"fix the flaky login test\"")
            .Respond("application/json", """{"url":"https://forge/issues/9"}""");

        var result = await sut.HandleAsync(capture, CancellationToken.None);

        result.ExternalRef.Should().Be("https://forge/issues/9");
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_IssueWithEmptyTitleAndNoBody_TruncatesLongContentToTitle()
    {
        var (sut, mock) = Build();
        var longContent = new string('x', 200);
        var capture = new Capture(
            Guid.NewGuid(), ChannelKind.Web, longContent, DateTimeOffset.UtcNow,
            LifecycleStage.Routed, "Bridge",
            Title: null, BridgeAlias: "br", BridgeAction: BridgeAction.Issue, BridgeBody: null);
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/issue")
            .WithPartialContent("\"title\":\"" + new string('x', 120) + "\"")
            .Respond("application/json", """{"url":"https://forge/issues/10"}""");

        var result = await sut.HandleAsync(capture, CancellationToken.None);

        result.ExternalRef.Should().Be("https://forge/issues/10");
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_IdeaWithEmptyBody_FallsBackToContentAsText()
    {
        var (sut, mock) = Build();
        var capture = new Capture(
            Guid.NewGuid(), ChannelKind.Web, "agp a fuzzy idea worth keeping", DateTimeOffset.UtcNow,
            LifecycleStage.Routed, "Bridge",
            Title: null, BridgeAlias: "agp", BridgeAction: BridgeAction.Idea, BridgeBody: null);
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/idea")
            .WithPartialContent("\"text\":\"agp a fuzzy idea worth keeping\"")
            .Respond("application/json", """{"url":"https://forge/ideas.md#z"}""");

        var result = await sut.HandleAsync(capture, CancellationToken.None);

        result.ExternalRef.Should().Be("https://forge/ideas.md#z");
        mock.VerifyNoOutstandingExpectation();
    }
}
