using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Audio;

namespace FlowHub.AI.IntegrationTests;

public class AiSpeechToTextTests
{
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
}
