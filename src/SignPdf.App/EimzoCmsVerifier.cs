using SignPdf.Eimzo;
using SignPdf.Pdf;

namespace SignPdf.App;

internal sealed class EimzoCmsVerifier : IPdfCmsVerifier
{
    private readonly EimzoClient _client;

    public EimzoCmsVerifier(EimzoClient client)
    {
        _client = client;
    }

    public async Task<CmsVerifyResult?> VerifyDetachedAsync(
        byte[] data,
        byte[] pkcs7,
        CancellationToken cancellationToken)
    {
        var info = await _client.GetDetachedPkcs7InfoAsync(data, pkcs7, cancellationToken)
            .ConfigureAwait(false);
        var chain = CmsCertChain.Extract(pkcs7);
        if (!info.Success)
        {
            if (info.FunctionMissing)
            {
                return await VerifyWithoutPkcs7InfoAsync(data, pkcs7, chain, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new CmsVerifyResult
            {
                SignatureValid = null,
                CertificateTrusted = false,
                Subject = chain.Subjects.FirstOrDefault() ?? "",
                Issuer = chain.Issuers.FirstOrDefault() ?? "",
                Source = "E-IMZO",
                Details = Loc.T("pkcs7_fail")
                          + Environment.NewLine
                          + info.Reason,
                Reason = info.Reason,
            };
        }

        var digestValid = InterpretDigest(info);
        var (trusted, trustDetails) = await ConfirmEimzoKeyAsync(info, chain, cancellationToken)
            .ConfigureAwait(false);
        var details = BuildDetails(info, digestValid, trusted, trustDetails, chain);

        return new CmsVerifyResult
        {
            SignatureValid = digestValid,
            CertificateTrusted = trusted,
            TrustDetails = trustDetails,
            Subject = info.Subject,
            Issuer = info.Issuer,
            SignedAt = info.SignedAt,
            NotBefore = info.NotBefore,
            NotAfter = info.NotAfter,
            Algorithm = info.Algorithm,
            Source = "E-IMZO",
            Details = details,
            Reason = SanitizeReason(info.Reason),
        };
    }

    private async Task<CmsVerifyResult> VerifyWithoutPkcs7InfoAsync(
        byte[] data,
        byte[] pkcs7,
        CmsCertChain chain,
        CancellationToken cancellationToken)
    {
        var local = CmsLocalInspect.Parse(pkcs7, data);
        var (trusted, trustDetails) = await ConfirmEimzoKeyAsync(
                new EimzoPkcs7Info { Success = true },
                chain,
                cancellationToken)
            .ConfigureAwait(false);
        var details = BuildDetails(
            new EimzoPkcs7Info { Success = true, Reason = "" },
            local.Integrity,
            trusted,
            trustDetails,
            chain,
            localIntegrity: true);

        return new CmsVerifyResult
        {
            SignatureValid = local.Integrity,
            CertificateTrusted = trusted,
            TrustDetails = trustDetails,
            Subject = FirstNonEmpty(local.Subject, chain.Subjects.FirstOrDefault()),
            Issuer = FirstNonEmpty(local.Issuer, chain.Issuers.FirstOrDefault()),
            SignedAt = local.SignedAt,
            NotBefore = local.NotBefore,
            NotAfter = local.NotAfter,
            Algorithm = local.Algorithm,
            Source = "E-IMZO",
            Details = details,
            Reason = "",
        };
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

    private async Task<(bool Trusted, string Details)> ConfirmEimzoKeyAsync(
        EimzoPkcs7Info info,
        CmsCertChain chain,
        CancellationToken cancellationToken)
    {
        if (info.CertificateVerified == true)
        {
            return (true, Loc.T("trust_ok"));
        }

        if (chain.Certificates.Count < 2)
        {
            return (false, Loc.T("trust_no_ca"));
        }

        for (var i = 0; i < chain.Certificates.Count - 1; i++)
        {
            var ok = await _client.VerifyCertificateIssuedByAsync(
                    chain.Certificates[i],
                    chain.Certificates[i + 1],
                    cancellationToken)
                .ConfigureAwait(false);
            if (!ok)
            {
                var subject = PreferCn(chain.Subjects[i]);
                var issuer = PreferCn(chain.Subjects[i + 1]);
                return (false, Loc.T("trust_issued", subject, issuer));
            }
        }

        if (chain.RootIsSelfSigned)
        {
            var rootOk = await _client.VerifyCertificateIssuedByAsync(
                    chain.Certificates[^1],
                    chain.Certificates[^1],
                    cancellationToken)
                .ConfigureAwait(false);
            if (!rootOk)
            {
                return (false, Loc.T("trust_root"));
            }
        }

        var names = string.Join(" ← ", chain.Subjects.Select(PreferCn));
        return (true, Loc.T("trust_chain", names));
    }

    private static bool? InterpretDigest(EimzoPkcs7Info info)
    {
        if (info.DigestValid == true || info.OverallVerified == true)
        {
            return true;
        }

        if (info.DigestValid == false)
        {
            return false;
        }

        if (IsContentMismatch(info.Reason))
        {
            return false;
        }

        if (info.CertificateVerified == false || IsTrustIssue(info.Reason))
        {
            return true;
        }

        if (info.OverallVerified == false)
        {
            return null;
        }

        return true;
    }

    private static string BuildDetails(
        EimzoPkcs7Info info,
        bool? digestValid,
        bool trusted,
        string trustDetails,
        CmsCertChain chain,
        bool localIntegrity = false)
    {
        var lines = new List<string>();
        if (digestValid == true)
        {
            lines.Add(localIntegrity ? Loc.T("integrity_ok_local") : Loc.T("integrity_ok"));
        }
        else if (digestValid == false)
        {
            lines.Add(Loc.T("integrity_bad"));
        }
        else
        {
            lines.Add(localIntegrity ? Loc.T("integrity_unclear_local") : Loc.T("integrity_unclear"));
        }

        if (trusted)
        {
            lines.Add(Loc.T("key_ok", trustDetails));
        }
        else
        {
            lines.Add(Loc.T("key_bad", trustDetails));
        }

        if (chain.Subjects.Count > 0)
        {
            lines.Add(Loc.T("certs_in_sig", string.Join(" ← ", chain.Subjects.Select(PreferCn))));
        }

        lines.Add(Loc.T("local_check"));

        var reason = SanitizeReason(info.Reason);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            lines.Add(Loc.T("eimzo_reply", reason));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string SanitizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "";
        }

        if (reason.Contains("TRUSTED CERTIFICATE PROVIDER", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("CertificatePathBuildException", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return reason;
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

    private static bool IsTrustIssue(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var text = reason.ToLowerInvariant();
        return text.Contains("trust", StringComparison.Ordinal)
               || text.Contains("цепоч", StringComparison.Ordinal)
               || text.Contains("удостоверяющ", StringComparison.Ordinal)
               || text.Contains("не доверен", StringComparison.Ordinal)
               || text.Contains("ocsp", StringComparison.Ordinal)
               || text.Contains("crl", StringComparison.Ordinal)
               || text.Contains("certificate", StringComparison.Ordinal);
    }

    private static bool IsContentMismatch(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var text = reason.ToLowerInvariant();
        return text.Contains("digest", StringComparison.Ordinal)
               || text.Contains("content", StringComparison.Ordinal)
               || text.Contains("message", StringComparison.Ordinal)
               || text.Contains("не совпад", StringComparison.Ordinal)
               || text.Contains("измен", StringComparison.Ordinal)
               || text.Contains("invalid signature", StringComparison.Ordinal);
    }
}
