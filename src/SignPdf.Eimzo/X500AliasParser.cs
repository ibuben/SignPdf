using System.Globalization;
using System.Text.RegularExpressions;

namespace SignPdf.Eimzo;

internal static class X500AliasParser
{
    private static readonly Regex AttributeSplit = new(@",(?=[A-Za-z0-9.]+=)", RegexOptions.Compiled);

    public static EimzoCertificate Parse(string disk, string path, string name, string alias)
    {
        alias ??= "";
        var normalized = alias
            .Replace("1.2.860.3.16.1.1=", "INN=", StringComparison.OrdinalIgnoreCase)
            .Replace("1.2.860.3.16.1.2=", "PINFL=", StringComparison.OrdinalIgnoreCase)
            .Replace("1.2.840.113549.1.9.1=", "E=", StringComparison.OrdinalIgnoreCase);

        return new EimzoCertificate
        {
            Disk = disk ?? "",
            Path = path ?? "",
            Name = name ?? "",
            Alias = alias,
            CommonName = Get(normalized, "CN"),
            GivenName = FirstNonEmpty(Get(normalized, "GIVENNAME"), Get(normalized, "GN"), Get(normalized, "NAME")),
            Surname = Get(normalized, "SURNAME"),
            Organization = Get(normalized, "O"),
            Inn = FirstNonEmpty(Get(normalized, "INN"), Get(normalized, "UID")),
            Pinfl = Get(normalized, "PINFL"),
            SerialNumber = Get(normalized, "SERIALNUMBER"),
            Email = FirstNonEmpty(
                Get(normalized, "E"),
                Get(normalized, "EMAIL"),
                Get(normalized, "EMAILADDRESS")),
            ValidFrom = ParseDate(Get(normalized, "VALIDFROM")),
            ValidTo = ParseDate(Get(normalized, "VALIDTO")),
        };
    }

    public static string Get(string source, string key)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "";
        }

        var parts = AttributeSplit.Split(source);
        foreach (var part in parts)
        {
            var item = part.Trim().TrimStart(',');
            var eq = item.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = item[..eq].Trim();
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return item[(eq + 1)..].Trim();
            }
        }

        return "";
    }

    private static DateTime? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        var space = normalized.IndexOf(' ');
        if (space > 0)
        {
            normalized = normalized[..space].Replace('.', '-') + "T" + normalized[(space + 1)..];
        }
        else
        {
            normalized = normalized.Replace('.', '-');
        }

        if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }
}
