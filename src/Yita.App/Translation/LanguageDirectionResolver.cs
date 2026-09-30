using System.Text;
using Yita.Settings;

namespace Yita.Translation;

internal static class LanguageDirectionResolver
{
    internal const string Chinese = "简体中文";
    internal const string English = "英语";

    public static string ResolveTargetLanguage(string sourceText, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.TargetLanguageMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return settings.TargetLanguage;
        }

        var chineseCount = 0;
        var englishCount = 0;
        foreach (var rune in sourceText.EnumerateRunes())
        {
            if (IsCjkIdeograph(rune.Value))
            {
                chineseCount++;
            }
            else if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                englishCount++;
            }
        }

        // Translate into the opposite of the dominant language. A
        // Chinese-dominant selection becomes English; anything else stays
        // Simplified Chinese.
        return chineseCount > englishCount ? English : Chinese;
    }

    public static string GetOppositeTarget(string currentTarget)
    {
        return currentTarget.Equals(English, StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    private static bool IsCjkIdeograph(int value)
    {
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2EBEF;
    }
}
