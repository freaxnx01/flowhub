using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace FlowHub.Skills.ContractTests.Bridge;

[Trait("Category", "SkillContract")]
public sealed class BridgeContractTests : IClassFixture<WireMockServerFixture>, IDisposable
{
    private const string Token = "bridge-token";

    private readonly WireMockServerFixture _wire;
    private readonly HttpClient _http;
    private readonly BridgeSkillIntegration _sut;

    public BridgeContractTests(WireMockServerFixture wire)
    {
        _wire = wire;
        _wire.Reset();
        _http = new HttpClient { BaseAddress = new Uri(_wire.BaseUrl) };
        _sut = new BridgeSkillIntegration(
            _http,
            Options.Create(new BridgeOptions { BaseUrl = _wire.BaseUrl, ApiToken = Token }),
            NullLogger<BridgeSkillIntegration>.Instance);
    }

    public void Dispose() => _http.Dispose();

    private static Capture IssueCapture() =>
        new(Guid.NewGuid(), ChannelKind.Web, "br the login 500s", DateTimeOffset.UtcNow,
            LifecycleStage.Routed, "Bridge", Title: "Login 500 on Safari",
            BridgeAlias: "br", BridgeAction: BridgeAction.Issue, BridgeBody: "The login endpoint returns 500.");

    [Fact]
    public async Task HandleAsync_Issue_SendsAliasTitleBodyAndBearer_OnExactPath()
    {
        _wire.Server
            .Given(Request.Create().WithPath("/api/capture/issue").UsingPost()
                .WithHeader("Authorization", $"Bearer {Token}"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"url":"https://forge/issues/42","number":42}"""));

        var result = await _sut.HandleAsync(IssueCapture(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().Be("https://forge/issues/42");

        var logged = _wire.Server.LogEntries.Should()
            .ContainSingle(e => e.RequestMessage.AbsolutePath == "/api/capture/issue").Subject;
        logged.RequestMessage.Method.Should().Be("POST");
        logged.RequestMessage.Body.Should().Contain("\"alias\":\"br\"");
        logged.RequestMessage.Body.Should().Contain("\"title\":\"Login 500 on Safari\"");
        logged.RequestMessage.Body.Should().Contain("\"body\":\"The login endpoint returns 500.\"");
    }

    [Fact]
    public async Task HandleAsync_Idea_SendsAliasAndText_OnExactPath()
    {
        _wire.Server
            .Given(Request.Create().WithPath("/api/capture/idea").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"url":"https://forge/ideas.md#abc"}"""));

        var capture = IssueCapture() with { BridgeAction = BridgeAction.Idea, BridgeAlias = "agp", BridgeBody = "what if repos had a health score" };
        var result = await _sut.HandleAsync(capture, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().Be("https://forge/ideas.md#abc");

        var logged = _wire.Server.LogEntries.Should()
            .ContainSingle(e => e.RequestMessage.AbsolutePath == "/api/capture/idea").Subject;
        logged.RequestMessage.Body.Should().Contain("\"alias\":\"agp\"");
        logged.RequestMessage.Body.Should().Contain("\"text\":\"what if repos had a health score\"");
    }

    [Fact]
    public async Task HandleAsync_UnknownAlias404_ThrowsHttpRequestException()
    {
        _wire.Server
            .Given(Request.Create().WithPath("/api/capture/issue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("unknown alias"));

        var act = () => _sut.HandleAsync(IssueCapture(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
