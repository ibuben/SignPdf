using System.IO;

namespace SignPdf.App;

internal static class PdfPathNames
{
    public static string ForDialog(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        return ReplaceDialogChars(name);
    }

    public static string ForPrintJob(string path)
    {
        var name = ForDialog(path);
        return string.IsNullOrWhiteSpace(name) ? "PDF" : name;
    }

    public static string PreferExistingPdf(string selected)
    {
        if (string.IsNullOrWhiteSpace(selected) || File.Exists(selected))
        {
            return selected;
        }

        string full;
        try
        {
            full = Path.GetFullPath(selected);
        }
        catch
        {
            return selected;
        }

        var dir = Path.GetDirectoryName(full);
        var leaf = Path.GetFileName(full);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(leaf) || !Directory.Exists(dir))
        {
            return selected;
        }

        List<string> matches;
        try
        {
            matches = Directory.EnumerateFiles(dir, "*.pdf")
                .Where(file => IsDialogTruncation(Path.GetFileName(file), leaf))
                .Take(2)
                .ToList();
        }
        catch
        {
            return selected;
        }

        return matches.Count == 1 ? matches[0] : selected;
    }

    private static bool IsDialogTruncation(string actual, string returned)
    {
        var cut = DialogCut(actual);
        if (cut <= 0)
        {
            return false;
        }

        var prefix = actual[..cut];
        var withExtension = prefix + Path.GetExtension(actual);
        return returned.Equals(prefix, StringComparison.OrdinalIgnoreCase)
               || returned.Equals(withExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static int DialogCut(string name)
    {
        var semi = name.IndexOf(';');
        var percent = name.IndexOf('%');
        if (semi < 0)
        {
            return percent;
        }

        if (percent < 0)
        {
            return semi;
        }

        return Math.Min(semi, percent);
    }

    private static string ReplaceDialogChars(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c < ' ' || c is ';' or '%' or '"' or '\\' or '/' or ':' or '*' or '?' or '<' or '>' or '|')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
