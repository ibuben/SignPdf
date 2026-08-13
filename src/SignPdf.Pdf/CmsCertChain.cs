using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using BcX509 = Org.BouncyCastle.X509.X509Certificate;

namespace SignPdf.Pdf;

public sealed class CmsCertChain
{
    public IReadOnlyList<byte[]> Certificates { get; init; } = Array.Empty<byte[]>();
    public IReadOnlyList<string> Subjects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Issuers { get; init; } = Array.Empty<string>();
    public bool RootIsSelfSigned { get; init; }

    public static CmsCertChain Extract(byte[] pkcs7)
    {
        CmsSignedData cms;
        try
        {
            cms = new CmsSignedData(DerUtil.Trim(pkcs7));
        }
        catch
        {
            return new CmsCertChain();
        }

        var store = cms.GetCertificates();
        var all = new List<BcX509>();
        foreach (BcX509 cert in store.EnumerateMatches(null))
        {
            all.Add(cert);
        }

        if (all.Count == 0)
        {
            return new CmsCertChain();
        }

        var leaf = FindLeaf(cms, all) ?? all[0];
        var chain = Walk(leaf, all);
        return new CmsCertChain
        {
            Certificates = chain.Select(c => c.GetEncoded()).ToArray(),
            Subjects = chain.Select(c => c.SubjectDN.ToString()).ToArray(),
            Issuers = chain.Select(c => c.IssuerDN.ToString()).ToArray(),
            RootIsSelfSigned = chain.Count > 0 && IsSelfSigned(chain[^1]),
        };
    }

    private static BcX509? FindLeaf(CmsSignedData cms, List<BcX509> all)
    {
        foreach (SignerInformation signer in cms.GetSignerInfos().GetSigners())
        {
            foreach (BcX509 match in cms.GetCertificates().EnumerateMatches(signer.SignerID))
            {
                return match;
            }
        }

        return all.FirstOrDefault(c => !IsSelfSigned(c)) ?? all[0];
    }

    private static List<BcX509> Walk(BcX509 leaf, List<BcX509> all)
    {
        var chain = new List<BcX509> { leaf };
        var current = leaf;
        var used = new HashSet<string>(StringComparer.Ordinal) { Convert.ToHexString(leaf.GetEncoded()) };

        while (!IsSelfSigned(current))
        {
            var issuer = all.FirstOrDefault(c =>
                !used.Contains(Convert.ToHexString(c.GetEncoded()))
                && SameName(c.SubjectDN, current.IssuerDN));
            if (issuer is null)
            {
                break;
            }

            chain.Add(issuer);
            used.Add(Convert.ToHexString(issuer.GetEncoded()));
            current = issuer;
            if (chain.Count > 8)
            {
                break;
            }
        }

        return chain;
    }

    private static bool IsSelfSigned(BcX509 cert) => SameName(cert.SubjectDN, cert.IssuerDN);

    private static bool SameName(X509Name left, X509Name right)
    {
        try
        {
            if (left.Equivalent(right))
            {
                return true;
            }
        }
        catch
        {
            // fall back to string compare
        }

        return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static byte[] MergeCertificates(byte[] pkcs7, IReadOnlyList<byte[]> extraCertificates)
    {
        if (extraCertificates.Count == 0)
        {
            return pkcs7;
        }

        try
        {
            var cms = new CmsSignedData(DerUtil.Trim(pkcs7));
        var certs = new List<BcX509>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BcX509 cert in cms.GetCertificates().EnumerateMatches(null))
        {
            if (seen.Add(Convert.ToHexString(cert.GetEncoded())))
            {
                certs.Add(cert);
            }
        }

        var parser = new X509CertificateParser();
        foreach (var raw in extraCertificates)
        {
            try
            {
                var cert = parser.ReadCertificate(DerUtil.Trim(raw));
                if (cert is not null && seen.Add(Convert.ToHexString(cert.GetEncoded())))
                {
                    certs.Add(cert);
                }
            }
            catch
            {
                // skip unreadable extra certs
            }
            }

            var store = new MemoryCertStore(certs);
            var updated = CmsSignedData.ReplaceCertificatesAndCrls(cms, store, cms.GetCrls());
            return updated.GetEncoded();
        }
        catch
        {
            return pkcs7;
        }
    }

    private sealed class MemoryCertStore : IStore<BcX509>
    {
        private readonly IReadOnlyList<BcX509> _certs;

        public MemoryCertStore(IReadOnlyList<BcX509> certs)
        {
            _certs = certs;
        }

        public IEnumerable<BcX509> EnumerateMatches(ISelector<BcX509>? selector)
        {
            foreach (var cert in _certs)
            {
                if (selector is null || selector.Match(cert))
                {
                    yield return cert;
                }
            }
        }
    }
}
