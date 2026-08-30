using System.Net;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;

namespace FlowHub.Skills.Tests.Bridge;

public sealed class BridgeCatalogReposTests
{
    private const string BaseUrl = "https://bridge.example.com";

    private const string Payload = """
        [
          {"name":"flowhub","alias":"fh","desc":"Capture anything.","topics":["dotnet"],"last_used":"2026-08-20T10:00:00Z"},
          {"name":"game-nibbles","desc":"Faithful browser Nibbles/Snake clone"},
          {"name":"bare-repo"}
        ]
        """;

    private static BridgeCatalog Build(MockHttpMessageHandler mock, MutableTimeProvider time)
    {
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri(BaseUrl);
        var options = Options.Create(new BridgeOptions
        {
            BaseUrl = BaseUrl,
            ApiToken = "tok",
            CatalogTtl = TimeSpan.FromMinutes(5),
        });
        return new BridgeCatalog(http, options, NullLogger<BridgeCatalog>.Instance, time);
    }

    [Fact]
    public async Task GetReposAsync_ReturnsNameAliasDescTopicsAndLastUsed()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", Payload);
        var sut = Build(mock, new MutableTimeProvider(DateTimeOffset.UtcNow));

        var repos = await sut.GetReposAsync(default);

        repos.Should().HaveCount(3);
        var flowhub = repos.Single(r => r.Name == "flowhub");
        flowhub.Alias.Should().Be("fh");
        flowhub.Desc.Should().Be("Capture anything.");
        flowhub.Topics.Should().ContainSingle().Which.Should().Be("dotnet");
        flowhub.LastUsed.Should().NotBeNull();
    }

    [Fact]
    public async Task GetReposAsync_ToleratesMissingDescTopicsAndAlias()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", Payload);
        var sut = Build(mock, new MutableTimeProvider(DateTimeOffset.UtcNow));

        var repos = await sut.GetReposAsync(default);

        var bare = repos.Single(r => r.Name == "bare-repo");
        bare.Alias.Should().BeNull();
        bare.Desc.Should().BeNull();
        bare.Topics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAliasesAsync_AndGetReposAsync_ShareOneFetch()
    {
        var mock = new MockHttpMessageHandler();
        var request = mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", Payload);
        var sut = Build(mock, new MutableTimeProvider(DateTimeOffset.UtcNow));

        await sut.GetAliasesAsync(default);
        await sut.GetReposAsync(default);

        mock.GetMatchCount(request).Should().Be(1);
    }

    [Fact]
    public async Task GetReposAsync_FirstFetchFails_ReturnsEmptyWithoutThrowing()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos").Respond(HttpStatusCode.InternalServerError);
        var sut = Build(mock, new MutableTimeProvider(DateTimeOffset.UtcNow));

        var repos = await sut.GetReposAsync(default);

        repos.Should().BeEmpty();
    }

    /// <summary>
    /// Hand-rolled controllable <see cref="TimeProvider"/>, matching the pattern used in
    /// <see cref="BridgeCatalogTests"/>.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
