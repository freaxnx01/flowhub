namespace FlowHub.Telegram;

/// <summary>
/// Configuration for the Telegram inbound Channel. Inactive unless both a bot token
/// and at least one allowed user id are present, so an unconfigured FlowHub never
/// contacts Telegram. Both values are secrets-adjacent — supply via environment
/// variables (<c>Telegram__BotToken</c>, <c>Telegram__AllowedUserIds</c>), never
/// appsettings.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Telegram";

    /// <summary>BotFather token. Secret.</summary>
    public string? BotToken { get; set; }

    /// <summary>Numeric Telegram user ids permitted to submit Captures.</summary>
    public IReadOnlyList<long> AllowedUserIds { get; set; } = [];

    /// <summary>True when the Channel has everything it needs to start.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) && AllowedUserIds.Count > 0;

    /// <summary>True when <paramref name="userId"/> may submit Captures.</summary>
    public bool IsAllowed(long userId) => AllowedUserIds.Contains(userId);
}
