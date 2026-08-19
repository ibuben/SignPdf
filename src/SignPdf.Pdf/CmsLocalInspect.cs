using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.CryptoPro;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using BcX509 = Org.BouncyCastle.X509.X509Certificate;

namespace SignPdf.Pdf;

public sealed class CmsLocalInspect
{
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public DateTime? NotBefore { get; init; }
    public DateTime? NotAfter { get; init; }
    public DateTime? SignedAt { get; init; }
    public string Algorithm { get; init; } = "";
    public bool? Integrity { get; init; }

    public static CmsLocalInspect Parse(byte[] cmsBytes, byte[] signedBytes)
    {
        if (cmsBytes.Length == 0)
        {
            return new CmsLocalInspect();
        }

        if (!TryOpenCms(cmsBytes, signedBytes, out var cms) || cms is null)
        {
            return new CmsLocalInspect();
        }

        SignerInformation? signer = null;
        foreach (SignerInformation item in cms.GetSignerInfos().GetSigners())
        {
            signer = item;
            break;
        }

        BcX509? cert = FindSignerCert(cms, signer);

        bool? integrity = null;
        if (signer is not null && cert is not null)
        {
            integrity = TryVerify(signer, cert, signedBytes);
        }

        DateTime? signedAt = null;
        try
        {
            if (signer?.SignedAttributes is not null)
            {
                var attr = signer.SignedAttributes[PkcsObjectIdentifiers.Pkcs9AtSigningTime];
                var first = attr?.AttrValues?[0];
                if (first is not null)
                {
                    signedAt = DateTime.Parse(first.ToString()!);
                }
            }
        }
        catch
        {
            // ignore
        }

        return new CmsLocalInspect
        {
            Subject = cert?.SubjectDN.ToString() ?? "",
            Issuer = cert?.IssuerDN.ToString() ?? "",
            NotBefore = cert?.NotBefore,
            NotAfter = cert?.NotAfter,
            SignedAt = signedAt,
            Algorithm = FirstNonEmpty(signer?.EncryptionAlgOid, signer?.DigestAlgOid),
            Integrity = integrity,
        };
    }

    private static bool TryOpenCms(byte[] cmsBytes, byte[] signedBytes, out CmsSignedData? cms)
    {
        try
        {
            cms = signedBytes.Length > 0
                ? new CmsSignedData(new CmsProcessableByteArray(signedBytes), DerUtil.Trim(cmsBytes))
                : new CmsSignedData(DerUtil.Trim(cmsBytes));
            return true;
        }
        catch
        {
            try
            {
                cms = new CmsSignedData(DerUtil.Trim(cmsBytes));
                return true;
            }
            catch
            {
                cms = null;
                return false;
            }
        }
    }

    private static BcX509? FindSignerCert(CmsSignedData cms, SignerInformation? signer)
    {
        var store = cms.GetCertificates();
        if (signer is not null)
        {
            foreach (BcX509 match in store.EnumerateMatches(signer.SignerID))
            {
                return match;
            }
        }

        foreach (BcX509 match in store.EnumerateMatches(null))
        {
            return match;
        }

        return null;
    }

    private static bool? TryVerify(SignerInformation signer, BcX509 cert, byte[] signedBytes)
    {
        try
        {
            return signer.Verify(cert);
        }
        catch
        {
            return OzdstCms.TryVerify(signer, cert, signedBytes);
        }
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
}

internal static class OzdstCms
{
    private static readonly Asn1ObjectIdentifier[] CryptoProCurveOids =
    {
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProA,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProB,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProC,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProXchA,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProXchB,
    };

    public static bool? TryVerify(SignerInformation signer, BcX509 cert, byte[] signedBytes)
    {
        try
        {
            var signedAttr = GetEncodedSignedAttributes(signer);
            var signature = signer.GetSignature();
            if (signature.Length == 0)
            {
                return null;
            }

            var signedOver = signedAttr is { Length: > 0 } ? signedAttr : signedBytes;
            foreach (var key in EnumeratePublicKeys(cert))
            {
                if (VerifyGostSignature(key, signedOver, signature))
                {
                    if (signedAttr is { Length: > 0 } && MessageDigestMatches(signer, signedBytes) == false)
                    {
                        return false;
                    }

                    return true;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? GetEncodedSignedAttributes(SignerInformation signer)
    {
        try
        {
            var encoded = signer.GetEncodedSignedAttributes();
            if (encoded is { Length: > 0 })
            {
                return encoded;
            }
        }
        catch
        {
            // older BouncyCastle builds may not expose the helper
        }

        return null;
    }

    private static bool? MessageDigestMatches(SignerInformation signer, byte[] signedBytes)
    {
        if (signer.SignedAttributes is null || signedBytes.Length == 0)
        {
            return null;
        }

        try
        {
            var attr = signer.SignedAttributes[PkcsObjectIdentifiers.Pkcs9AtMessageDigest];
            if (attr?.AttrValues is null)
            {
                return null;
            }

            var expected = Asn1OctetString.GetInstance(attr.AttrValues[0]).GetOctets();
            if (expected is not { Length: > 0 })
            {
                return null;
            }

            foreach (var actual in EnumerateDigests(signedBytes, expected.Length))
            {
                if (expected.AsSpan().SequenceEqual(actual))
                {
                    return true;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<byte[]> EnumerateDigests(byte[] data, int size)
    {
        if (size is 32)
        {
            yield return Digest(new Gost3411Digest(), data);
            yield return Digest(new Gost3411_2012_256Digest(), data);
        }
        else if (size is 64)
        {
            yield return Digest(new Gost3411_2012_512Digest(), data);
        }
        else
        {
            yield return Digest(new Gost3411Digest(), data);
        }
    }

    private static byte[] Digest(Org.BouncyCastle.Crypto.IDigest digest, byte[] data)
    {
        digest.BlockUpdate(data, 0, data.Length);
        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return hash;
    }

    private static IEnumerable<ECPublicKeyParameters> EnumeratePublicKeys(BcX509 cert)
    {
        SubjectPublicKeyInfo? spki;
        try
        {
            spki = TbsCertificateStructure.GetInstance(cert.GetTbsCertificate()).SubjectPublicKeyInfo;
        }
        catch
        {
            yield break;
        }

        byte[] keyBytes;
        try
        {
            keyBytes = spki.PublicKeyData.GetOctets();
        }
        catch
        {
            try
            {
                keyBytes = spki.PublicKeyData.GetBytes();
            }
            catch
            {
                yield break;
            }
        }

        foreach (var curve in EnumerateCurves(spki))
        {
            var key = TryEcKey(keyBytes, curve);
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static IEnumerable<ECDomainParameters> EnumerateCurves(SubjectPublicKeyInfo spki)
    {
        foreach (var oid in EnumerateParameterOids(spki))
        {
            var named = ECGost3410NamedCurves.GetByOid(oid);
            if (named is not null)
            {
                yield return named;
            }
        }

        foreach (var oid in CryptoProCurveOids)
        {
            ECDomainParameters? named = null;
            try
            {
                named = ECGost3410NamedCurves.GetByOid(oid);
            }
            catch
            {
                // ignore unknown oid
            }

            if (named is not null)
            {
                yield return named;
            }
        }
    }

    private static IEnumerable<Asn1ObjectIdentifier> EnumerateParameterOids(SubjectPublicKeyInfo spki)
    {
        Asn1Encodable? parameters;
        try
        {
            parameters = spki.Algorithm.Parameters;
        }
        catch
        {
            yield break;
        }

        if (parameters is null)
        {
            yield break;
        }

        if (parameters is Asn1ObjectIdentifier single)
        {
            yield return single;
            yield break;
        }

        try
        {
            var sequence = Asn1Sequence.GetInstance(parameters.ToAsn1Object());
            for (var i = 0; i < sequence.Count; i++)
            {
                if (sequence[i] is Asn1ObjectIdentifier oid)
                {
                    yield return oid;
                }
            }
        }
        catch
        {
            // not a sequence of OIDs
        }
    }

    private static ECPublicKeyParameters? TryEcKey(byte[] keyBytes, ECDomainParameters curve)
    {
        try
        {
            ECPoint? point = null;
            if (keyBytes.Length == 64)
            {
                var x = UnsignedLe(keyBytes, 0, 32);
                var y = UnsignedLe(keyBytes, 32, 32);
                point = curve.Curve.CreatePoint(x, y);
            }
            else if (keyBytes.Length == 65 && keyBytes[0] == 0x04)
            {
                point = curve.Curve.DecodePoint(keyBytes);
            }
            else if (keyBytes.Length is 32 or 33 or 65)
            {
                point = curve.Curve.DecodePoint(keyBytes);
            }

            if (point is null || point.IsInfinity)
            {
                return null;
            }

            return new ECPublicKeyParameters(point, curve);
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifyGostSignature(ECPublicKeyParameters key, byte[] data, byte[] signature)
    {
        if (signature.Length % 2 != 0 || signature.Length < 64)
        {
            return VerifyWithDigestSigner(key, data, signature);
        }

        var half = signature.Length / 2;
        foreach (var hash in EnumerateDigests(data, half))
        {
            foreach (var (r, s) in EnumerateRs(signature, half))
            {
                try
                {
                    var gost = new ECGOST3410Signer();
                    gost.Init(false, key);
                    if (gost.VerifySignature(hash, r, s))
                    {
                        return true;
                    }
                }
                catch
                {
                    // try next encoding
                }
            }
        }

        return VerifyWithDigestSigner(key, data, signature);
    }

    private static bool VerifyWithDigestSigner(ECPublicKeyParameters key, byte[] data, byte[] signature)
    {
        try
        {
            var verifier = new Gost3410DigestSigner(new ECGOST3410Signer(), new Gost3411Digest());
            verifier.Init(false, key);
            verifier.BlockUpdate(data, 0, data.Length);
            return verifier.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<(BigInteger R, BigInteger S)> EnumerateRs(byte[] signature, int half)
    {
        yield return (UnsignedLe(signature, 0, half), UnsignedLe(signature, half, half));
        yield return (UnsignedLe(signature, half, half), UnsignedLe(signature, 0, half));
        yield return (UnsignedBe(signature, 0, half), UnsignedBe(signature, half, half));
        yield return (UnsignedBe(signature, half, half), UnsignedBe(signature, 0, half));
    }

    private static BigInteger UnsignedLe(byte[] data, int offset, int length)
    {
        var rev = new byte[length];
        for (var i = 0; i < length; i++)
        {
            rev[i] = data[offset + length - 1 - i];
        }

        return new BigInteger(1, rev);
    }

    private static BigInteger UnsignedBe(byte[] data, int offset, int length)
    {
        var slice = new byte[length];
        Buffer.BlockCopy(data, offset, slice, 0, length);
        return new BigInteger(1, slice);
    }
}
