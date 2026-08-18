using System.Net;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;

namespace FlowHub.Skills.Tests.Bridge;

public sealed class BridgeCatalogTests
{
    private const string BaseUrl = "https://bridge.example.com";

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
    public async Task GetAliasesAsync_ReturnsLowercasedNonEmptyAliases()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", """[{"alias":"BR"},{"alias":"agp"},{"alias":null},{"alias":""},{"name":"no-alias-repo"}]""");
        var sut = Build(mock, new MutableTimeProvider(DateTimeOffset.UtcNow));

        var aliases = await sut.GetAliasesAsync(default);

        aliases.Should().BeEquivalentTo(new[] { "br", "agp" });
    }

    [Fact]
    public async Task GetAliasesAsync_WithinTtl_DoesNotRefetch()
    {
        var mock = new MockHttpMessageHandler();
        var request = mock.Expect(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", """[{"alias":"br"}]""");
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(mock, time);

        await sut.GetAliasesAsync(default);
        time.Advance(TimeSpan.FromMinutes(1));
        await sut.GetAliasesAsync(default);

        mock.VerifyNoOutstandingExpectation(); // exactly one GET satisfied the Expect
        mock.GetMatchCount(request).Should().Be(1);
    }

    [Fact]
    public async Task GetAliasesAsync_AfterTtl_Refetches()
    {
        var mock = new MockHttpMessageHandler();
        var request = mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", """[{"alias":"br"}]""");
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(mock, time);

        await sut.GetAliasesAsync(default);
        time.Advance(TimeSpan.FromMinutes(6));
        await sut.GetAliasesAsync(default);

        mock.GetMatchCount(request).Should().Be(2);
    }

    [Fact]
    public async Task GetAliasesAsync_FirstFetchFails_ReturnsEmptyWithoutThrowing()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos").Respond(HttpStatusCode.InternalServerError);
        var sut = Build(mock, new MutableTimeProvider(DateTimeOffset.UtcNow));

        var aliases = await sut.GetAliasesAsync(default);

        aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAliasesAsync_RefreshFails_KeepsLastKnownSet()
    {
        var mock = new MockHttpMessageHandler();
        mock.Expect(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", """[{"alias":"br"}]""");
        mock.Expect(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond(HttpStatusCode.InternalServerError);
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(mock, time);

        await sut.GetAliasesAsync(default); // seeds cache with { br }

        time.Advance(TimeSpan.FromMinutes(6));
        var aliases = await sut.GetAliasesAsync(default); // refresh fails, cache kept

        aliases.Should().BeEquivalentTo(new[] { "br" });
    }

    /// <summary>
    /// Hand-rolled controllable <see cref="TimeProvider"/>. The repo does not reference
    /// Microsoft.Extensions.TimeProvider.Testing, so we model a mutable "now" here.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
