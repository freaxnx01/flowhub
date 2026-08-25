namespace FlowHub.Telegram.Tests;

public class TelegramOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("123:ABC", true)]
    public void IsConfigured_RequiresBotTokenAndAtLeastOneAllowedUser(string? token, bool expected)
    {
        var options = new TelegramOptions { BotToken = token, AllowedUserIds = [42L] };

        options.IsConfigured.Should().Be(expected);
    }

    [Fact]
    public void IsConfigured_WithTokenButNoAllowedUsers_IsFalse()
    {
        var options = new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [] };

        options.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_UserOnTheList_IsTrue()
    {
        var options = new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [42L, 43L] };

        options.IsAllowed(43L).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_UserNotOnTheList_IsFalse()
    {
        var options = new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [42L] };

        options.IsAllowed(99L).Should().BeFalse();
    }
}
