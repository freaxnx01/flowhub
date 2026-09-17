using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiSensitivityScreenTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly FakeLogger<AiSensitivityScreen> _log = new();
    private readonly ChatOptions _opts = new() { MaxOutputTokens = 300, Temperature = 0.2f };

    private AiSensitivityScreen Sut() =>
        new(_chat, _log, _opts, new AiModelInfo("OpenRouter", "test-model"));

    private static ChatResponse JsonResponse(object payload) =>
        new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload)));

    private void Responds(object payload) =>
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(payload));

    [Theory]
    [InlineData("sensitive", Sensitivity.Sensitive)]
    [InlineData("unsure", Sensitivity.Unsure)]
    [InlineData("safe", Sensitivity.Safe)]
    public async Task ScreenAsync_WellFormedResponse_MapsTheVerdict(string answer, Sensitivity expected)
    {
        Responds(new { verdict = answer, reason = "because" });

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(expected);
    }

    [Fact]
    public async Task ScreenAsync_SafeVerdict_HasEmptyReason()
    {
        Responds(new { verdict = "safe", reason = "because" });

        var result = await Sut().ScreenAsync("anything", default);

        result.Reason.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenAsync_MalformedJson_ReturnsUnsureAndDoesNotThrow()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json at all")));

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(Sensitivity.Unsure);
        result.Reason.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ScreenAsync_ProviderThrows_ReturnsUnsureAndDoesNotThrow()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new HttpRequestException("provider down"));

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(Sensitivity.Unsure);
        result.Reason.Should().Contain("HttpRequestException");
    }

    [Fact]
    public async Task ScreenAsync_UnrecognisedVerdictString_ReturnsUnsure()
    {
        Responds(new { verdict = "definitely-fine", reason = "because" });

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(Sensitivity.Unsure);
    }

    [Fact]
    public async Task ScreenAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new OperationCanceledException(cts.Token));

        // Shutdown is not a screen failure — the capture is simply not processed yet.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Sut().ScreenAsync("anything", cts.Token));
    }

    [Fact]
    public async Task ScreenAsync_ForwardsCancellationTokenToChatClient()
    {
        using var cts = new CancellationTokenSource();
        Responds(new { verdict = "safe", reason = "" });

        await Sut().ScreenAsync("anything", cts.Token);

        await _chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), cts.Token);
    }

    // ---------------------------------------------------------------------------
    // Test infrastructure
    // ---------------------------------------------------------------------------

    internal sealed record LogRecord(LogLevel Level, EventId EventId, string Message);

    internal sealed class FakeLogger<T> : ILogger<T>
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add(new LogRecord(logLevel, eventId, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
