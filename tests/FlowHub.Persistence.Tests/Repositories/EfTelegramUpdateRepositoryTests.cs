using FlowHub.Core.Channels;
using FlowHub.Persistence.Repositories;
using FlowHub.Persistence.Tests.Fixtures;

namespace FlowHub.Persistence.Tests.Repositories;

[Collection(PostgresGroup.Name)]
public sealed class EfTelegramUpdateRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ExistsAsync_AfterRecord_IsTrue()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);

        await sut.RecordAsync(new TelegramUpdate(1001L, 55L, 7, Guid.NewGuid(), DateTimeOffset.UtcNow));

        (await sut.ExistsAsync(1001L)).Should().BeTrue();
        (await sut.ExistsAsync(1002L)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_SameUpdateTwice_DoesNotThrow()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);
        var update = new TelegramUpdate(2001L, 55L, 7, null, DateTimeOffset.UtcNow);

        await sut.RecordAsync(update);
        var act = async () => await sut.RecordAsync(update);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FindByCaptureIdAsync_ReturnsCoordinates()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);
        var captureId = Guid.NewGuid();
        await sut.RecordAsync(new TelegramUpdate(3001L, 88L, 9, captureId, DateTimeOffset.UtcNow));

        var found = await sut.FindByCaptureIdAsync(captureId);

        found.Should().NotBeNull();
        found!.ChatId.Should().Be(88L);
        found.MessageId.Should().Be(9);
    }

    [Fact]
    public async Task GetLastProcessedUpdateIdAsync_UsesProcessedAt_NotMaxUpdateId()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);
        var early = DateTimeOffset.UtcNow.AddMinutes(-5);
        await sut.RecordAsync(new TelegramUpdate(9999L, 1L, 1, null, early));
        await sut.RecordAsync(new TelegramUpdate(10L, 1L, 2, null, DateTimeOffset.UtcNow));

        // 10 was processed later than 9999 — the random-id-after-a-week case.
        (await sut.GetLastProcessedUpdateIdAsync()).Should().Be(10L);
    }
}
