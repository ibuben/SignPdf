namespace SignPdf.Pdf;

internal static class DerUtil
{
    public static byte[] Trim(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x30)
        {
            return data;
        }

        var offset = 1;
        var lenByte = data[offset++];
        int length;
        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            var count = lenByte & 0x7F;
            if (count == 0 || count > 4 || offset + count > data.Length)
            {
                return data;
            }

            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | data[offset++];
            }
        }

        var total = offset + length;
        if (total <= 0 || total > data.Length)
        {
            return data;
        }

        if (total == data.Length)
        {
            return data;
        }

        var trimmed = new byte[total];
        Buffer.BlockCopy(data, 0, trimmed, 0, total);
        return trimmed;
    }
}

internal static class CryptoOidNames
{
    public static string Describe(string? oid)
    {
        if (string.IsNullOrWhiteSpace(oid))
        {
            return "";
        }

        var name = oid switch
        {
            "1.2.860.3.15.1.3.2.1" => "O'zDSt 1092:2009-2",
            "1.2.860.3.15.1.3.2.1.1" => "O'zDSt 1106:2009-2-A",
            "1.2.860.3.15.1.3.1.1" => "O'zDSt 1092:2009-1",
            "1.2.860.3.15.1.1.2.1" => "O'zDSt 1106:2009",
            "1.2.860.3.15.1.1.1.1" => "O'zDSt 1106:2009",
            "1.2.860.3.15.1.1.2.2.2.2" => "O'zDSt 1106:2009",
            "1.2.643.7.1.1.3.2" => "GOST R 34.10-2012 256",
            "1.2.643.7.1.1.3.3" => "GOST R 34.10-2012 512",
            "1.2.643.7.1.1.2.2" => "GOST R 34.11-2012 256",
            "1.2.643.7.1.1.2.3" => "GOST R 34.11-2012 512",
            "1.2.840.113549.1.1.11" => "SHA256withRSA",
            "1.2.840.113549.1.1.5" => "SHA1withRSA",
            _ => "",
        };

        if (string.IsNullOrEmpty(name) && oid.StartsWith("1.2.860.", StringComparison.Ordinal))
        {
            return "O'zDSt / E-IMZO (" + oid + ")";
        }

        return string.IsNullOrEmpty(name) ? oid : $"{name} ({oid})";
    }
}
