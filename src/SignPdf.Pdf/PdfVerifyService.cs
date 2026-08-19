using System.Globalization;
using System.Text;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace SignPdf.Pdf;

public sealed class PdfVerifyService
{
    static PdfVerifyService()
    {
        _ = iText.Bouncycastleconnector.BouncyCastleFactoryCreator.GetFactory();
    }

    public PdfVerificationResult Verify(string filePath)
    {
        return VerifyAsync(filePath).GetAwaiter().GetResult();
    }

    public async Task<PdfVerificationResult> VerifyAsync(
        string filePath,
        IPdfCmsVerifier? cmsVerifier = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("PDF-файл не найден.", filePath);
        }

        using var reader = new PdfReader(filePath);
        using var pdf = new PdfDocument(reader);
        var util = new SignatureUtil(pdf);
        var names = util.GetSignatureNames();
        var coverage = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            coverage[name] = CoverageEnd(util.GetSignatureDictionary(name));
        }

        var checks = new List<SignatureCheck>(names.Count);

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var followedByLaterSignature = coverage.Any(pair => pair.Value > coverage[name]);
            checks.Add(await VerifyOneAsync(
                    filePath, util, name, followedByLaterSignature, cmsVerifier, cancellationToken)
                .ConfigureAwait(false));
        }

        string summary;
        if (checks.Count == 0)
        {
            summary = "В документе нет цифровых подписей.";
        }
        else if (checks.All(c => c.Status is SignatureStatus.Valid or SignatureStatus.SubsequentChanges))
        {
            summary = SubsequentSummary(checks, trusted: true);
        }
        else if (checks.Any(c => c.Status == SignatureStatus.KeyNotConfirmed)
                 && checks.All(c => c.Status is SignatureStatus.Valid
                                    or SignatureStatus.SubsequentChanges
                                    or SignatureStatus.KeyNotConfirmed
                                    or SignatureStatus.NotConfirmed))
        {
            summary = "Содержимое после подписи не менялось, но ключ системы E-IMZO не подтверждён (нет цепочки УЦ).";
        }
        else if (checks.All(c => c.Status is SignatureStatus.Valid
                                 or SignatureStatus.SubsequentChanges
                                 or SignatureStatus.NotConfirmed))
        {
            summary = "Подпись разобрана, но E-IMZO не дал однозначного подтверждения.";
        }
        else
        {
            summary = "Есть подписи с ошибками или ограничениями. Смотрите таблицу ниже.";
        }

        return new PdfVerificationResult
        {
            FilePath = filePath,
            SignatureCount = checks.Count,
            Signatures = checks,
            Summary = summary,
        };
    }

    public int CountSignatures(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("PDF-файл не найден.", filePath);
        }

        using var reader = new PdfReader(filePath);
        using var pdf = new PdfDocument(reader);
        return new SignatureUtil(pdf).GetSignatureNames().Count;
    }

    private static async Task<SignatureCheck> VerifyOneAsync(
        string filePath,
        SignatureUtil util,
        string name,
        bool followedByLaterSignature,
        IPdfCmsVerifier? cmsVerifier,
        CancellationToken cancellationToken)
    {
        var coversWhole = util.SignatureCoversWholeDocument(name);
        var dict = util.GetSignatureDictionary(name);
        var cmsBytes = ReadCmsBytes(filePath, dict);
        var signedBytes = ReadByteRange(filePath, dict);
        var parsed = TryParseCms(cmsBytes, signedBytes);

        if (cmsVerifier is not null && cmsBytes.Length > 0)
        {
            try
            {
                var eimzo = await cmsVerifier.VerifyDetachedAsync(signedBytes, cmsBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (eimzo is not null)
                {
                    var algorithm = CryptoOidNames.Describe(
                        FirstNonEmpty(eimzo.Algorithm, parsed?.Algorithm));
                    var extra = string.IsNullOrWhiteSpace(eimzo.Details)
                        ? "Проверено локально через E-IMZO."
                        : eimzo.Details;
                    return BuildCheck(
                        name,
                        coversWhole,
                        followedByLaterSignature,
                        eimzo.SignatureValid,
                        eimzo.CertificateTrusted,
                        FirstNonEmpty(eimzo.Subject, parsed?.Subject),
                        FirstNonEmpty(eimzo.Issuer, parsed?.Issuer),
                        eimzo.SignedAt ?? parsed?.SignedAt,
                        eimzo.NotBefore ?? parsed?.NotBefore,
                        eimzo.NotAfter ?? parsed?.NotAfter,
                        algorithm,
                        extra);
                }
            }
            catch (Exception ex)
            {
                parsed ??= TryParseCms(cmsBytes, signedBytes);
                if (parsed is not null)
                {
                    return BuildCheck(
                        name,
                        coversWhole,
                        followedByLaterSignature,
                        parsed.Integrity,
                        certificateTrusted: false,
                        parsed.Subject,
                        parsed.Issuer,
                        parsed.SignedAt,
                        parsed.NotBefore,
                        parsed.NotAfter,
                        CryptoOidNames.Describe(parsed.Algorithm),
                        "E-IMZO не смог проверить подпись:" + Environment.NewLine + ex.Message,
                        algorithmUnsupported: parsed.Integrity is null);
                }
            }
        }

        try
        {
            var pkcs7 = util.ReadSignatureData(name);
            var cert = pkcs7.GetSigningCertificate();
            var integrity = pkcs7.VerifySignatureIntegrityAndAuthenticity();
            return BuildCheck(
                name,
                coversWhole,
                followedByLaterSignature,
                integrity,
                certificateTrusted: integrity,
                cert?.GetSubjectDN()?.ToString() ?? parsed?.Subject ?? "",
                cert?.GetIssuerDN()?.ToString() ?? parsed?.Issuer ?? "",
                SafeSignDate(pkcs7) ?? parsed?.SignedAt,
                cert?.GetNotBefore() ?? parsed?.NotBefore,
                cert?.GetNotAfter() ?? parsed?.NotAfter,
                CryptoOidNames.Describe(SafeAlgorithm(pkcs7) ?? parsed?.Algorithm),
                "");
        }
        catch (Exception ex)
        {
            if (parsed is null)
            {
                return new SignatureCheck
                {
                    FieldName = name,
                    Status = SignatureStatus.AlgorithmUnsupported,
                    StatusText = StatusText(SignatureStatus.AlgorithmUnsupported),
                    CoversWholeDocument = coversWhole,
                    FollowedByLaterSignature = followedByLaterSignature,
                    Details = "Не удалось разобрать CMS." + Environment.NewLine + ex.Message,
                };
            }

            return BuildCheck(
                name,
                coversWhole,
                followedByLaterSignature,
                parsed.Integrity,
                certificateTrusted: false,
                parsed.Subject,
                parsed.Issuer,
                parsed.SignedAt,
                parsed.NotBefore,
                parsed.NotAfter,
                CryptoOidNames.Describe(parsed.Algorithm),
                parsed.Integrity is null
                    ? "Встроенная проверка не знает этот алгоритм."
                      + Environment.NewLine
                      + "Запустите E-IMZO — приложение проверит подпись через него."
                      + Environment.NewLine
                      + ex.GetType().Name
                    : "",
                algorithmUnsupported: parsed.Integrity is null);
        }
    }

    private static SignatureCheck BuildCheck(
        string name,
        bool coversWhole,
        bool followedByLaterSignature,
        bool? integrity,
        bool certificateTrusted,
        string subject,
        string issuer,
        DateTime? signedAt,
        DateTime? notBefore,
        DateTime? notAfter,
        string algorithm,
        string extra,
        bool algorithmUnsupported = false)
    {
        var now = DateTime.Now;
        SignatureStatus status;
        if (algorithmUnsupported)
        {
            status = SignatureStatus.AlgorithmUnsupported;
        }
        else if (integrity == false)
        {
            status = SignatureStatus.DocumentModified;
        }
        else if (integrity is null)
        {
            status = SignatureStatus.NotConfirmed;
        }
        else if (notAfter is { } after && now > after)
        {
            status = SignatureStatus.CertificateExpired;
        }
        else if (notBefore is { } before && now < before)
        {
            status = SignatureStatus.CertificateNotYetValid;
        }
        else if (!certificateTrusted)
        {
            status = SignatureStatus.KeyNotConfirmed;
        }
        else if (!coversWhole)
        {
            status = SignatureStatus.SubsequentChanges;
        }
        else
        {
            status = SignatureStatus.Valid;
        }

        return new SignatureCheck
        {
            FieldName = name,
            Status = status,
            StatusText = StatusText(status, followedByLaterSignature),
            SignerName = PersonNameText.ToTitleCase(PreferCn(subject)),
            Issuer = PreferCn(issuer),
            SignedAt = signedAt,
            ValidFrom = notBefore,
            ValidTo = notAfter,
            CoversWholeDocument = coversWhole,
            FollowedByLaterSignature = followedByLaterSignature,
            Algorithm = algorithm,
            Details = extra,
        };
    }

    private static CmsInfo? TryParseCms(byte[] cmsBytes, byte[] signedBytes)
    {
        var local = CmsLocalInspect.Parse(cmsBytes, signedBytes);
        if (string.IsNullOrWhiteSpace(local.Subject)
            && string.IsNullOrWhiteSpace(local.Algorithm)
            && local.Integrity is null)
        {
            return null;
        }

        return new CmsInfo(
            local.Subject,
            local.Issuer,
            local.NotBefore,
            local.NotAfter,
            local.SignedAt,
            local.Algorithm,
            local.Integrity);
    }

    private static byte[] ReadByteRange(string filePath, PdfDictionary sigDict)
    {
        var byteRange = sigDict.GetAsArray(PdfName.ByteRange);
        if (byteRange is null || byteRange.Size() < 2)
        {
            return Array.Empty<byte>();
        }

        using var input = File.OpenRead(filePath);
        using var output = new MemoryStream();
        for (var i = 0; i < byteRange.Size(); i += 2)
        {
            var start = byteRange.GetAsNumber(i).LongValue();
            var length = byteRange.GetAsNumber(i + 1).LongValue();
            input.Seek(start, SeekOrigin.Begin);
            CopyExact(input, output, length);
        }

        return output.ToArray();
    }

    private static byte[] ReadCmsBytes(string filePath, PdfDictionary sigDict)
    {
        try
        {
            var fromFile = ReadCmsFromByteRangeGap(filePath, sigDict);
            if (fromFile.Length > 0 && fromFile[0] == 0x30)
            {
                return DerUtil.Trim(fromFile);
            }
        }
        catch
        {
            // fall back to dictionary bytes
        }

        return DerUtil.Trim(sigDict.GetAsString(PdfName.Contents)?.GetValueBytes() ?? Array.Empty<byte>());
    }

    private static byte[] ReadCmsFromByteRangeGap(string filePath, PdfDictionary sigDict)
    {
        var byteRange = sigDict.GetAsArray(PdfName.ByteRange);
        if (byteRange is null || byteRange.Size() < 4)
        {
            return Array.Empty<byte>();
        }

        var firstEnd = byteRange.GetAsNumber(0).LongValue() + byteRange.GetAsNumber(1).LongValue();
        var secondStart = byteRange.GetAsNumber(2).LongValue();
        var gap = secondStart - firstEnd;
        if (gap is < 4 or > 10_000_000)
        {
            return Array.Empty<byte>();
        }

        using var input = File.OpenRead(filePath);
        input.Seek(firstEnd, SeekOrigin.Begin);
        var raw = new byte[gap];
        var read = input.Read(raw, 0, raw.Length);
        var ascii = Encoding.ASCII.GetString(raw, 0, read);
        var start = ascii.IndexOf('<');
        var end = ascii.LastIndexOf('>');
        if (start < 0 || end <= start)
        {
            return Array.Empty<byte>();
        }

        var hex = ascii[(start + 1)..end]
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("\t", "", StringComparison.Ordinal);
        return HexToBytes(hex);
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 == 1)
        {
            hex += "0";
        }

        var result = new byte[hex.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static void CopyExact(Stream input, Stream output, long length)
    {
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static DateTime? SafeSignDate(PdfPKCS7 pkcs7)
    {
        try
        {
            var date = pkcs7.GetSignDate();
            return date == DateTime.MinValue ? null : date;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeAlgorithm(PdfPKCS7 pkcs7)
    {
        try
        {
            return pkcs7.GetSignatureMechanismOid()
                   ?? pkcs7.GetDigestAlgorithmName()
                   ?? pkcs7.GetSignatureAlgorithmName();
        }
        catch
        {
            return null;
        }
    }

    private static string PreferCn(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return "";
        }

        foreach (var part in dn.Split(','))
        {
            var item = part.Trim();
            if (item.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return item[3..].Trim();
            }
        }

        return dn;
    }

    private static class PersonNameText
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
                return word.ToUpperInvariant();
            }

            var lower = word.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower[1..];
        }
    }

    private static string SubsequentSummary(IReadOnlyList<SignatureCheck> checks, bool trusted)
    {
        if (!checks.Any(c => c.Status == SignatureStatus.SubsequentChanges))
        {
            return trusted
                ? "Содержимое не менялось, ключ системы E-IMZO подтверждён локально."
                : "Содержимое после подписи не менялось.";
        }

        var laterChanges = checks.Where(c => c.Status == SignatureStatus.SubsequentChanges).ToList();
        if (laterChanges.All(c => c.FollowedByLaterSignature))
        {
            return trusted
                ? "Содержимое не менялось, ключ E-IMZO подтверждён. Документ подписан несколько раз: после части подписей добавили ещё одну подпись."
                : "Документ подписан несколько раз: после части подписей добавили ещё одну подпись.";
        }

        return trusted
            ? "Содержимое подписанных байт не менялось, ключ E-IMZO подтверждён, но после части подписей файл дополняли без новой подписи."
            : "После части подписей файл дополняли без новой подписи.";
    }

    private static long CoverageEnd(PdfDictionary sigDict)
    {
        var byteRange = sigDict.GetAsArray(PdfName.ByteRange);
        if (byteRange is null || byteRange.Size() < 4)
        {
            return 0;
        }

        return byteRange.GetAsNumber(2).LongValue() + byteRange.GetAsNumber(3).LongValue();
    }

    private static string FirstNonEmpty(params string?[] values)
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

    private static string StatusText(SignatureStatus status, bool followedByLaterSignature = false) => status switch
    {
        SignatureStatus.Valid => "Действительна",
        SignatureStatus.DocumentModified => "Документ изменён",
        SignatureStatus.CertificateExpired => "Сертификат истёк",
        SignatureStatus.CertificateNotYetValid => "Сертификат ещё не действует",
        SignatureStatus.SubsequentChanges => followedByLaterSignature
            ? "Верна, затем ещё подпись"
            : "Верна, есть поздние правки",
        SignatureStatus.AlgorithmUnsupported => "Алгоритм не разобран",
        SignatureStatus.KeyNotConfirmed => "Ключ не подтверждён",
        SignatureStatus.NotConfirmed => "Не подтверждена",
        SignatureStatus.Invalid => "Недействительна",
        SignatureStatus.Error => "Ошибка проверки",
        _ => status.ToString(),
    };

    private sealed record CmsInfo(
        string Subject,
        string Issuer,
        DateTime? NotBefore,
        DateTime? NotAfter,
        DateTime? SignedAt,
        string Algorithm,
        bool? Integrity);
}
