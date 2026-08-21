namespace Screenshot.App.Editor;

/// <summary>
/// Curated sticker set rendered from the system color emoji font. Strings are
/// standard emoji sequences (with VS16 where the character is text-default),
/// so the stickers look exactly as lively as they do in chat apps while the
/// application ships no image assets at all. Everything here is Emoji 12.0 or
/// older, which the minimum supported Windows build (19041) fully covers.
/// </summary>
public static class EmojiStickerCatalog
{
    public const string Default = "😊";

    public static IReadOnlyList<string> All { get; } =
    [
        "😀", "😁", "😂", "🤣", "😅", "😆", "😉", "😊",
        "😋", "😎", "😍", "😘", "🥰", "🙂", "🤗", "🤩",
        "🤔", "🙄", "😏", "😴", "😌", "😜", "🤪", "😝",
        "😒", "😔", "🙃", "🤑", "😲", "😖", "😞", "😤",
        "😢", "😭", "😨", "😩", "🤯", "😬", "😱", "🥵",
        "🥶", "😳", "🥺", "😡", "😠", "🤬", "😇", "🤠",
        "🤡", "🤫", "🧐", "😷", "😈", "🥳", "🥴", "💩",
        "👍", "👎", "👌", "✌️", "🤞", "🤟", "👏", "🙌",
        "🙏", "💪", "🤝", "❤️", "💔", "💕", "💖", "💯",
        "💢", "💥", "🔥", "✨", "⭐", "🎉", "🎊", "🏆",
        "🚀", "⚡", "🌈", "⚠️", "❗", "❓", "✅", "❌",
    ];
}
