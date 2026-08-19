using System.Globalization;
using System.Text.Json.Nodes;

namespace SignPdf.Eimzo;

public sealed class EimzoPkcs7Info
{
    public bool Success { get; init; }
    public bool FunctionMissing { get; init; }
    public string Reason { get; init; } = "";
    public bool? DigestValid { get; init; }
    public bool? CertificateVerified { get; init; }
    public bool? OverallVerified { get; init; }
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public DateTime? SignedAt { get; init; }
    public DateTime? NotBefore { get; init; }
    public DateTime? NotAfter { get; init; }
    public string Algorithm { get; init; } = "";
    public string RawJson { get; init; } = "";

    public static EimzoPkcs7Info FromResponse(JsonObject response)
    {
        var info = FindInfoObject(response);
        var signer = FindFirstSigner(info);
        var cert = FirstCertificate(signer);

        var timestamp = signer?["timestamp"] as JsonObject ?? signer?["timeStamp"] as JsonObject;
        // verified — общий вердикт (ЭЦП + цепочка). Не путать с целостностью файла.
        var digestValid = ReadBool(signer, "digestVerified")
                          ?? ReadBool(timestamp, "digestVerified");
        var certificateVerified = ReadBool(signer, "certificateVerified")
                                  ?? ReadBool(info, "certificateVerified");
        var overallVerified = ReadBool(signer, "verified")
                              ?? ReadBool(info, "verified");
        var exception = FirstNonEmpty(
            ReadString(signer, "exception"),
            ReadString(info, "exception"),
            ReadString(response, "reason"));

        return new EimzoPkcs7Info
        {
            Success = true,
            DigestValid = digestValid,
            CertificateVerified = certificateVerified,
            OverallVerified = overallVerified,
            Reason = exception,
            Subject = FirstNonEmpty(
                ReadString(cert, "subjectName"),
                ReadNested(cert, "subjectInfo", "CN"),
                ReadString(signer, "subjectName"),
                ReadString(signer, "signer")),
            Issuer = FirstNonEmpty(
                ReadString(cert, "issuerName"),
                ReadNested(cert, "issuerInfo", "CN"),
                ReadString(signer, "issuerName")),
            SignedAt = ReadDate(signer, "signingTime") ?? ReadDate(signer, "timestamp"),
            NotBefore = ReadDate(cert, "validFrom") ?? ReadDate(cert, "notBefore"),
            NotAfter = ReadDate(cert, "validTo") ?? ReadDate(cert, "notAfter"),
            Algorithm = FirstNonEmpty(
                ReadNested(cert, "signature", "signAlgName"),
                ReadNested(cert, "publicKey", "keyAlgName"),
                ReadString(signer, "signatureAlgorithm"),
                ReadString(signer, "digestAlgorithm")),
            RawJson = response.ToJsonString(),
        };
    }

    private static JsonObject? FindInfoObject(JsonObject response)
    {
        foreach (var key in new[] { "pkcs7Info", "pkcs7_info", "info", "data" })
        {
            if (response[key] is JsonObject obj)
            {
                return obj;
            }
        }

        return response;
    }

    private static JsonObject? FindFirstSigner(JsonObject? info)
    {
        if (info is null)
        {
            return null;
        }

        foreach (var key in new[] { "signers", "signatures" })
        {
            if (info[key] is JsonArray { Count: > 0 } array && array[0] is JsonObject first)
            {
                return first;
            }

            if (info[key] is JsonObject map)
            {
                foreach (var pair in map)
                {
                    if (pair.Value is JsonObject obj)
                    {
                        return obj;
                    }
                }
            }
        }

        if (info["certificate"] is not null || info["verified"] is not null)
        {
            return info;
        }

        return null;
    }

    private static JsonObject? FirstCertificate(JsonObject? signer)
    {
        if (signer?["certificate"] is JsonArray { Count: > 0 } array && array[0] is JsonObject first)
        {
            return first;
        }

        return signer?["certificate"] as JsonObject;
    }

    private static bool? ReadBool(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number != 0;
        }

        return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static string ReadString(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value)
        {
            return "";
        }

        return value.TryGetValue<string>(out var text) ? text : value.ToString();
    }

    private static string ReadNested(JsonObject? obj, string parent, string child)
    {
        if (obj?[parent] is JsonObject nested)
        {
            return ReadString(nested, child);
        }

        return "";
    }

    private static DateTime? ReadDate(JsonObject? obj, string name)
    {
        var text = ReadString(obj, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return "";
    }
}
