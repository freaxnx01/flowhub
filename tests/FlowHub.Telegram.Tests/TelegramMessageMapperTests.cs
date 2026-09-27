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

    private static Update CallbackUpdate(Message? message) => new()
    {
        Id = 9,
        CallbackQuery = new CallbackQuery
        {
            Id = "cbq-1",
            From = new User { Id = 42 },
            Data = "legend",
            Message = message,
        },
    };

    [Fact]
    public void MapCallback_ButtonTap_ProducesACallbackWithItsCoordinates()
    {
        var callback = TelegramMessageMapper.MapCallback(
            CallbackUpdate(new Message { Id = 8, Chat = new Chat { Id = 55 } }));

        callback.Should().Be(new TelegramCallback(
            UpdateId: 9, CallbackQueryId: "cbq-1", ChatId: 55, MessageId: 8, FromUserId: 42, Data: "legend"));
    }

    [Fact]
    public void MapCallback_WithoutTheOriginatingMessage_ReturnsNull()
    {
        TelegramMessageMapper.MapCallback(CallbackUpdate(message: null)).Should().BeNull();
    }

    [Fact]
    public void Map_CallbackUpdate_IsNotAMessage()
    {
        TelegramMessageMapper.Map(CallbackUpdate(new Message { Id = 8, Chat = new Chat { Id = 55 } })).Should().BeNull();
    }
}
