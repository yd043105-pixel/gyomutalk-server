using System.Text.RegularExpressions;

namespace SchoolMessenger.Contracts;

public sealed record MessageRun(string Text, string Font = "Malgun Gothic", double Size = 12, bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false, string? Color = null, string? Highlight = null);
public sealed record MessageFormat(MessageRun[] Runs);

public static class MessageFormatRules
{
    public static bool FontAllowed(string? font) => font is { Length: > 0 and <= 100 } && Regex.IsMatch(font, @"\A[\p{L}\p{N} ._()\-]+\z");
    static bool ColorAllowed(string? color) => color is null || Regex.IsMatch(color, @"\A#[0-9a-fA-F]{6}\z");
    public static bool Valid(MessageFormat? format, string body) => format is null || format.Runs is { Length: > 0 and <= 1000 } runs &&
        runs.All(r => r is not null && r.Text is { Length: <= 20_000 } && FontAllowed(r.Font) && double.IsFinite(r.Size) && r.Size is >= 6 and <= 72 && ColorAllowed(r.Color) && ColorAllowed(r.Highlight)) &&
        string.Concat(runs.Select(r => r.Text)) == body;
}
