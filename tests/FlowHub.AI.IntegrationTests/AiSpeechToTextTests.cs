using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Audio;

namespace FlowHub.AI.IntegrationTests;

public class AiSpeechToTextTests
{
    /// <summary>
    /// Builds a transcription result the way the SDK intends for tests —
    /// OpenAIAudioModelFactory exists for exactly this.
    /// </summary>
    private static System.ClientModel.ClientResult<AudioTranscription> Result(string text)
    {
        // OPENAI001: the model factory is marked experimental, but it is the SDK's own
        // sanctioned way to build a result for a test. Suppressed at the single call
        // site rather than project-wide, so a future SDK change surfaces here.
#pragma warning disable OPENAI001
        var transcription = OpenAIAudioModelFactory.AudioTranscription(text: text);
#pragma warning restore OPENAI001
        return System.ClientModel.ClientResult.FromValue(
            transcription, Substitute.For<System.ClientModel.Primitives.PipelineResponse>());
    }

    [Fact]
    public async Task TranscribeAsync_ProviderThrows_ReturnsNullRatherThanPropagating()
    {
        // The consumer turns null into an Orphan; an exception here would instead
        // surface as a MassTransit fault and be retried as if transient.
        var client = Substitute.For<AudioClient>();
        client.TranscribeAudioAsync(Arg.Any<Stream>(), Arg.Any<string>(),
                Arg.Any<AudioTranscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<System.ClientModel.ClientResult<AudioTranscription>>>(
                _ => throw new HttpRequestException("provider down"));
        var sut = new AiSpeechToText(client, NullLogger<AiSpeechToText>.Instance);

        using var audio = new MemoryStream(new byte[8]);
        var result = await sut.TranscribeAsync(audio, "voice.ogg");

        result.Should().BeNull();
    }

    [Fact]
    public async Task TranscribeAsync_RealCancellation_PropagatesRatherThanReturningNull()
    {
        // Swallowing a genuine cancellation turns a shutdown or per-message timeout into
        // a terminal Orphan, when it should surface as a retryable fault. RepoResolver.cs
        // and ZitateEnricher.cs both special-case this; so must we.
        var client = Substitute.For<AudioClient>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        client.TranscribeAudioAsync(Arg.Any<Stream>(), Arg.Any<string>(),
                Arg.Any<AudioTranscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<System.ClientModel.ClientResult<AudioTranscription>>>(
                _ => throw new TaskCanceledException());
        var sut = new AiSpeechToText(client, NullLogger<AiSpeechToText>.Instance);

        using var audio = new MemoryStream(new byte[8]);
        var act = async () => await sut.TranscribeAsync(audio, "voice.ogg", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TranscribeAsync_ProviderTimeoutWithLiveToken_ReturnsNull()
    {
        // A TaskCanceledException with no cancellation requested is an HTTP timeout,
        // not caller intent — that one is a transcription failure.
        var client = Substitute.For<AudioClient>();
        client.TranscribeAudioAsync(Arg.Any<Stream>(), Arg.Any<string>(),
                Arg.Any<AudioTranscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<System.ClientModel.ClientResult<AudioTranscription>>>(
                _ => throw new TaskCanceledException());
        var sut = new AiSpeechToText(client, NullLogger<AiSpeechToText>.Instance);

        using var audio = new MemoryStream(new byte[8]);
        var result = await sut.TranscribeAsync(audio, "voice.ogg", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TranscribeAsync_Success_ReturnsTheTrimmedTranscript()
    {
        var client = Substitute.For<AudioClient>();
        client.TranscribeAudioAsync(Arg.Any<Stream>(), Arg.Any<string>(),
                Arg.Any<AudioTranscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result("  buy milk on the way home  ")));
        var sut = new AiSpeechToText(client, NullLogger<AiSpeechToText>.Instance);

        using var audio = new MemoryStream(new byte[8]);
        var result = await sut.TranscribeAsync(audio, "voice.ogg");

        result.Should().Be("buy milk on the way home");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TranscribeAsync_EmptyTranscript_ReturnsNull(string text)
    {
        // Silence should become a visible failure, not an empty Capture.
        var client = Substitute.For<AudioClient>();
        client.TranscribeAudioAsync(Arg.Any<Stream>(), Arg.Any<string>(),
                Arg.Any<AudioTranscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result(text)));
        var sut = new AiSpeechToText(client, NullLogger<AiSpeechToText>.Instance);

        using var audio = new MemoryStream(new byte[8]);
        var result = await sut.TranscribeAsync(audio, "voice.ogg");

        result.Should().BeNull();
    }


}
