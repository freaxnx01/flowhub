using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassifierTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly IClassifier _keyword = Substitute.For<IClassifier>();
    private readonly FakeLogger<AiClassifier> _log = new();
    private readonly ChatOptions _opts = new() { MaxOutputTokens = 300, Temperature = 0.2f };
    private readonly IVikunjaProjectCatalog _catalog = Substitute.For<IVikunjaProjectCatalog>();

    public AiClassifierTests()
    {
        _catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Inbox"] = 1, ["Zitate"] = 7 });
    }

    private AiClassifier Sut() =>
        new(_chat, _keyword, _log, _opts, _catalog, new AiModelInfo("OpenRouter", "test-model"), new EmptyBridgeCatalog());

    private static ChatResponse JsonResponse(object payload) =>
        new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload)));

    [Fact]
    public async Task ClassifyAsync_AiSucceedsWithValidSchema_ReturnsAiResult()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "link", "article" },
                 matched_skill = "Wallabag",
                 title = "Saving an article for later",
             }));

        var result = await Sut().ClassifyAsync("https://example.com/article", default);

        result.MatchedSkill.Should().Be("Wallabag");
        result.Tags.Should().BeEquivalentTo(new[] { "link", "article" });
        result.Title.Should().Be("Saving an article for later");
        await _keyword.DidNotReceive().ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_ForwardsCancellationTokenToChatClient()
    {
        using var cts = new CancellationTokenSource();
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new { tags = new[] { "x" }, matched_skill = "", title = (string?)null }));

        await Sut().ClassifyAsync("anything", cts.Token);

        await _chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            cts.Token);
    }

    [Fact]
    public async Task ClassifyAsync_PassesMaxOutputTokens300_ToChatClient()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new { tags = new[] { "x" }, matched_skill = "", title = (string?)null }));

        await Sut().ClassifyAsync("anything", default);

        await _chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.MaxOutputTokens == 300),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_PassesTemperature02_ToChatClient()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new { tags = new[] { "x" }, matched_skill = "", title = (string?)null }));

        await Sut().ClassifyAsync("anything", default);

        await _chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && Math.Abs((double)(o.Temperature ?? 0f) - 0.2) < 0.0001),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_HttpRequestException_FallsBackToKeyword()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new HttpRequestException("network down"));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ClassificationResult(["unsorted"], string.Empty));

        var result = await Sut().ClassifyAsync("anything", default);

        result.Should().BeEquivalentTo(new ClassificationResult(["unsorted"], string.Empty));
        await _keyword.Received(1).ClassifyAsync("anything", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_TaskCanceledException_FallsBackToKeyword()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new TaskCanceledException("HttpClient timeout"));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ClassificationResult(["unsorted"], string.Empty));

        var result = await Sut().ClassifyAsync("anything", default);

        result.MatchedSkill.Should().BeEmpty();
        await _keyword.Received(1).ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_JsonException_FallsBackToKeyword()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "this is not JSON")));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ClassificationResult(["unsorted"], string.Empty));

        var result = await Sut().ClassifyAsync("anything", default);

        result.MatchedSkill.Should().BeEmpty();
        await _keyword.Received(1).ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_MatchedSkillOutsideAllowedSet_FallsBackAndLogsSchemaViolation()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new { tags = new[] { "x" }, matched_skill = "Bogus", title = "t" }));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ClassificationResult(["unsorted"], string.Empty));

        var result = await Sut().ClassifyAsync("anything", default);

        result.MatchedSkill.Should().BeEmpty();
        _log.Records.Should().ContainSingle(r => r.EventId.Id == 3010 && r.Message.Contains("schema_violation"));
    }

    [Fact]
    public async Task ClassifyAsync_GenericException_FallsBackToKeyword()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new InvalidOperationException("anything else"));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ClassificationResult(["unsorted"], string.Empty));

        var result = await Sut().ClassifyAsync("anything", default);

        result.MatchedSkill.Should().BeEmpty();
        await _keyword.Received(1).ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_OnFallback_LogsEventId3010WithExceptionTypeAndDuration()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new HttpRequestException("oops"));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ClassificationResult(["unsorted"], string.Empty));

        await Sut().ClassifyAsync("anything", default);

        var record = _log.Records.Should().ContainSingle(r => r.EventId.Id == 3010).Subject;
        record.Level.Should().Be(LogLevel.Warning);
        record.Message.Should().Contain(nameof(HttpRequestException));
        record.Message.Should().MatchRegex(@"duration_ms=\d+");
    }

    [Fact]
    public async Task ClassifyAsync_PropagatesProjectAndEntitiesFromModel()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "quote", "computing" },
                 matched_skill = "Vikunja",
                 title = "Gabriel on Unix and C",
                 project = "Zitate",
                 entities = new[]
                 {
                     new { key = "quote", value = "Unix and C are the ultimate computer viruses." },
                     new { key = "author", value = "Richard Gabriel" },
                 },
             }));

        var result = await Sut().ClassifyAsync("\"Unix and C…\" — Richard Gabriel", default);

        result.MatchedSkill.Should().Be("Vikunja");
        result.VikunjaProject.Should().Be("Zitate");
        result.Entities.Should().NotBeNull();
        result.Entities!["author"].Should().Be("Richard Gabriel");
    }

    [Fact]
    public async Task ClassifyAsync_DuplicateEntityKeys_LastOneWins()
    {
        // The array shape can carry a key twice; the dictionary it converts into cannot.
        // A duplicate is model noise, not a reason to fail the capture — and last-wins
        // matches MergeEntities' own `merged[key] = value` semantics.
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "quote" },
                 matched_skill = "Vikunja",
                 title = "A quote with a repeated key",
                 project = "Zitate",
                 entities = new[]
                 {
                     new { key = "author", value = "First" },
                     new { key = "author", value = "Second" },
                 },
             }));

        var result = await Sut().ClassifyAsync("a quote", default);

        result.Entities.Should().NotBeNull();
        result.Entities!["author"].Should().Be("Second");
    }

    [Fact]
    public async Task ClassifyAsync_EntityWithMissingKey_IsSkippedAndTheCaptureStillClassifies()
    {
        // AiEntity.Key is a non-nullable reference type, but System.Text.Json does not
        // enforce that: {"value":"…"} with no key deserialises to a null Key. Indexing a
        // dictionary with it throws, and AiClassifier's broad catch turns one malformed
        // entity into a keyword-classified capture — the failure this issue removes. The
        // old Dictionary<string,string> made this impossible; JSON keys cannot be null.
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "quote" },
                 matched_skill = "Vikunja",
                 title = "A quote with a malformed entity",
                 project = "Zitate",
                 entities = new object[]
                 {
                     new { value = "Goethe" },
                     new { key = "author", value = "Richard Gabriel" },
                 },
             }));

        var result = await Sut().ClassifyAsync("a quote", default);

        result.MatchedSkill.Should().Be("Vikunja");
        result.Entities.Should().NotBeNull();
        result.Entities!["author"].Should().Be("Richard Gabriel");
    }

    [Fact]
    public async Task ClassifyAsync_NullEntityElement_IsSkippedAndTheCaptureStillClassifies()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "quote" },
                 matched_skill = "Vikunja",
                 title = "A quote with a null entity",
                 project = "Zitate",
                 entities = new object?[]
                 {
                     null,
                     new { key = "author", value = "Richard Gabriel" },
                 },
             }));

        var result = await Sut().ClassifyAsync("a quote", default);

        result.MatchedSkill.Should().Be("Vikunja");
        result.Entities!["author"].Should().Be("Richard Gabriel");
    }

    [Fact]
    public async Task ClassifyAsync_OnlyMalformedEntities_YieldsNoEntitiesRatherThanAnEmptyMap()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "quote" },
                 matched_skill = "Vikunja",
                 title = "Every entity malformed",
                 project = "Zitate",
                 entities = new object[] { new { value = "orphaned" } },
             }));

        var result = await Sut().ClassifyAsync("a quote", default);

        result.MatchedSkill.Should().Be("Vikunja");
        result.Entities.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_SkippedMalformedEntity_IsLogged()
    {
        // Dropping model output silently is invisible data loss. The skip is the right
        // behaviour; being unable to tell it happened is not.
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "quote" },
                 matched_skill = "Vikunja",
                 title = "A quote with a malformed entity",
                 project = "Zitate",
                 entities = new object[]
                 {
                     new { value = "no key here" },
                     new { key = "author", value = "Richard Gabriel" },
                 },
             }));

        await Sut().ClassifyAsync("a quote", default);

        _log.Records.Should().Contain(r => r.EventId.Id == 3012);
    }

    // Every DTO the AI layer sends through GetResponseAsync<T>. The regression value is
    // in catching a *future* dictionary member: a dictionary added to any of these would
    // reintroduce the 400 with nothing else failing.
    [Theory]
    [InlineData(typeof(AiClassificationResponse))]
    [InlineData(typeof(AiBridgeResponse))]
    [InlineData(typeof(AiSensitivityResponse))]
    [InlineData(typeof(AiRepoConfirmResponse))]
    public void StructuredOutputSchema_ContainsNoAdditionalPropertiesSchemaObject(Type dto)
    {
        // Anthropic requires additionalProperties to be `false` for an object and rejects
        // a schema there with HTTP 400. A Dictionary<string,string> member generates
        // "additionalProperties": {"type":"string"}, which took every capture down to the
        // keyword classifier. See docs/superpowers/specs/2026-09-20-classifier-entities-schema-design.md
        //
        // NOTE: this builds the schema with CreateJsonSchema's default options, not the
        // options GetResponseAsync<T> uses to build its ChatResponseFormat. The two agree
        // on dictionary-vs-array today, so the guard is valid — but it is a proxy for the
        // sent schema, not the sent schema itself. If a Microsoft.Extensions.AI upgrade
        // diverges the two paths, this stops covering what it claims to.
        var schema = AIJsonUtilities.CreateJsonSchema(dto);

        var offenders = new List<string>();
        Walk(schema, "$", offenders);

        offenders.Should().BeEmpty(
            "every `additionalProperties` must be the literal false, not a schema object");

        static void Walk(JsonElement node, string path, List<string> offenders)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("additionalProperties")
                        && prop.Value.ValueKind is not JsonValueKind.False)
                    {
                        offenders.Add($"{path}.additionalProperties");
                    }

                    Walk(prop.Value, $"{path}.{prop.Name}", offenders);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, $"{path}[{i++}]", offenders);
                }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Test infrastructure — kept in the same file for locality
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
