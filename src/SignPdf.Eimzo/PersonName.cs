using System.Globalization;

namespace SignPdf.Eimzo;

public static class PersonName
{
    public static string ToTitleCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var words = value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = TitleWord(words[i]);
        }

        return string.Join(" ", words);
    }

    private static string TitleWord(string word)
    {
        if (word.Contains('-', StringComparison.Ordinal))
        {
            return string.Join("-", word.Split('-').Select(TitleWord));
        }

        if (word.Length == 1)
        {
            return word.ToUpper(CultureInfo.InvariantCulture);
        }

        var lower = word.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}
