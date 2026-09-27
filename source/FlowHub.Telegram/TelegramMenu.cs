namespace FlowHub.Telegram;

/// <summary>
/// The bot's command menu. Adding a function is one button here plus its case in
/// <see cref="TelegramCallbackHandler"/>.
/// </summary>
public static class TelegramMenu
{
    public const string Command = "menu";

    /// <summary>Telegram sends /start when a chat with the bot is first opened.</summary>
    public const string StartCommand = "start";

    public const string LegendCallbackData = "legend";

    public const string Text = "What would you like to see?";

    public const string UnknownCommandText = "Unknown command — try /menu.";

    public static IReadOnlyList<TelegramMenuButton> Buttons { get; } =
    [
        new("Emojis", LegendCallbackData),
    ];
}
