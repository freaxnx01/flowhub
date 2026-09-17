using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FlowHub.Web.ComponentTests.Ai;

/// <summary>
/// Fixtures reproducing the SHAPES the corpus survey found, with entirely invented
/// content. Fixture 4 is the one that matters: material that reads as a mundane note
/// and is in fact care/therapy content. A keyword rule cannot catch it, which is why
/// the screen is a model call rather than a regex.
/// </summary>
public sealed class SensitivityFixtureTests
{
    public static TheoryData<string, string> SensitiveShapes() => new()
    {
        { "care journal", "Mittwoch war ein besserer Tag, weniger Weinen beim Abholen, Erzieherin meint es wird langsam ruhiger" },
        { "medication note", "neue Dosis ab Montag, halbe Tablette morgens, Kontrolle in vier Wochen" },
        { "third-party profile", "Sandra, 41, getrennt seit letztem Jahr, zwei Kinder, arbeitet Teilzeit, sucht gerade eine neue Wohnung" },
        { "unmarked care material", "beim Autofahren redet sie viel mehr, ohne Blickkontakt geht es ihr leichter — das nächste Mal wieder so versuchen" },
    };

    public static TheoryData<string, string> SafeShapes() => new()
    {
        { "link", "https://example.com/an-article-worth-reading" },
        { "errand", "Batterien kaufen" },
        { "dev task", "bridge mcp from outside LAN?" },
        { "media", "Mr Bean Filme" },
    };

    private static AiSensitivityScreen ScreenRespondingWith(string verdict)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                JsonSerializer.Serialize(new { verdict, reason = "category" }))));

        return new AiSensitivityScreen(
            chat,
            new FakeLogger<AiSensitivityScreen>(),
            new ChatOptions { MaxOutputTokens = 200, Temperature = 0.2f },
            new AiModelInfo("OpenRouter", "test-model"));
    }

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

    [Theory]
    [MemberData(nameof(SensitiveShapes))]
    public async Task SensitiveShape_WhenModelSaysSensitive_IsWithheldWithACategoryReason(string shape, string content)
    {
        var result = await ScreenRespondingWith("sensitive").ScreenAsync(content, default);

        result.Verdict.Should().Be(Sensitivity.Sensitive, because: $"the {shape} shape must never route");
        result.Reason.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(SensitiveShapes))]
    public async Task SensitiveShape_WhenModelIsUnsure_StillParks(string shape, string content)
    {
        var result = await ScreenRespondingWith("unsure").ScreenAsync(content, default);

        result.Verdict.Should().NotBe(Sensitivity.Safe, because: $"doubt about the {shape} shape parks");
    }

    [Theory]
    [MemberData(nameof(SafeShapes))]
    public async Task SafeShape_WhenModelSaysSafe_Routes(string shape, string content)
    {
        var result = await ScreenRespondingWith("safe").ScreenAsync(content, default);

        result.Verdict.Should().Be(Sensitivity.Safe, because: $"the {shape} shape is ordinary");
        result.Reason.Should().BeEmpty();
    }

    [Fact]
    public void SensitivityPrompt_NamesTheUnmarkedCase()
    {
        // The single most important line in the prompt: the corpus's most dangerous
        // capture had no medical vocabulary at all. If this instruction is ever edited
        // away, the screen silently stops catching that class.
        var messages = AiPrompts.BuildSensitivityMessages("x");
        var system = messages[0].Text;

        system.Should().Contain("without any medical or");
        system.Should().Contain("prefer \"unsure\"");
    }
}
