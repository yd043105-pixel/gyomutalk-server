using System.Globalization;
using System.Text.RegularExpressions;

namespace SchoolMessenger.Shared;

public static class MessageText
{
    public static string Excerpt(string? text, int length = 120)
    {
        var value = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (value.Length <= length) return value;
        var characters = StringInfo.ParseCombiningCharacters(value);
        var end = System.Array.FindLast(characters, index => index <= length);
        return value[..end] + "…";
    }
}
