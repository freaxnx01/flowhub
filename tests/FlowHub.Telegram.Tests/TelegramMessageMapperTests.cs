using Telegram.Bot.Types;

namespace FlowHub.Telegram.Tests;

public class TelegramMessageMapperTests
{
    private static Update VoiceUpdate(int duration, string mime = "audio/ogg") => new()
    {
        Id = 7,
        Message = new Message
        {
            Id = 3,
            Chat = new Chat { Id = 55 },
            From = new User { Id = 42 },
            Voice = new Voice { FileId = "voice-abc", Duration = duration, MimeType = mime, FileSize = 2048 },
        },
    };

    [Fact]
    public void Map_VoiceMessage_ProducesAFileWithDurationAndMimeType()
    {
        var message = TelegramMessageMapper.Map(VoiceUpdate(12));

        message.Should().NotBeNull();
        message!.File.Should().NotBeNull();
        message.File!.FileId.Should().Be("voice-abc");
        message.File.ContentType.Should().Be("audio/ogg");
        message.File.DurationSeconds.Should().Be(12);
    }

    [Fact]
    public void Map_VoiceMessage_WithNoMimeType_FallsBackToAudioOgg()
    {
        var update = VoiceUpdate(5);
        update.Message!.Voice!.MimeType = null;

        var message = TelegramMessageMapper.Map(update);

        message!.File!.ContentType.Should().Be("audio/ogg");
    }
}
