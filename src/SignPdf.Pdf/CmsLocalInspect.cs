using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.CryptoPro;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
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
            integrity = TryVerify(signer, cert, signedBytes, cmsBytes);
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

    private static bool? TryVerify(SignerInformation signer, BcX509 cert, byte[] signedBytes, byte[] cmsBytes)
    {
        try
        {
            if (signer.Verify(cert))
            {
                return true;
            }
        }
        catch
        {
            // O'zDSt OIDs are unknown to BouncyCastle; verify locally.
        }

        return OzdstCms.TryVerify(signer, cert, signedBytes, cmsBytes);
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
    private static readonly DerObjectIdentifier[] CryptoProCurveOids =
    {
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProA,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProB,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProC,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProXchA,
        CryptoProObjectIdentifiers.GostR3410x2001CryptoProXchB,
    };

    public static bool? TryVerify(SignerInformation signer, BcX509 cert, byte[] signedBytes, byte[] cmsBytes)
    {
        try
        {
            var signature = signer.GetSignature();
            if (signature.Length == 0)
            {
                return null;
            }

            var payloads = EnumeratePayloads(signer, signedBytes, cmsBytes).ToArray();
            var keys = EnumeratePublicKeys(cert).ToArray();
            if (keys.Length == 0)
            {
                return null;
            }

            var attrVerified = false;
            var digestMismatch = false;
            foreach (var payload in payloads)
            {
                foreach (var key in keys)
                {
                    if (!VerifyGostSignature(key, payload.Bytes, signature))
                    {
                        continue;
                    }

                    if (!payload.IsSignedAttributes)
                    {
                        return true;
                    }

                    attrVerified = true;
                    var digest = MessageDigestMatches(signer, signedBytes);
                    if (digest == true)
                    {
                        return true;
                    }

                    if (digest == false)
                    {
                        digestMismatch = true;
                    }
                }
            }

            if (digestMismatch)
            {
                return false;
            }

            return attrVerified ? true : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<(byte[] Bytes, bool IsSignedAttributes)> EnumeratePayloads(
        SignerInformation signer,
        byte[] signedBytes,
        byte[] cmsBytes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attrs in EnumerateSignedAttributeEncodings(signer, cmsBytes))
        {
            if (attrs is { Length: > 0 } && seen.Add(Convert.ToHexString(attrs)))
            {
                yield return (attrs, true);
            }
        }

        if (signedBytes.Length > 0)
        {
            yield return (signedBytes, false);
        }
    }

    private static IEnumerable<byte[]> EnumerateSignedAttributeEncodings(SignerInformation signer, byte[] cmsBytes)
    {
        var original = ExtractOriginalSignedAttributesRaw(cmsBytes)
                       ?? ExtractOriginalSignedAttributes(cmsBytes);
        if (original is { Length: > 0 })
        {
            yield return original;
        }

        var encoded = GetEncodedSignedAttributes(signer);
        if (encoded is { Length: > 0 })
        {
            yield return encoded;
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

    private static byte[]? ExtractOriginalSignedAttributesRaw(byte[] cmsBytes)
    {
        try
        {
            var data = DerUtil.Trim(cmsBytes);
            var contentInfo = ReadTlv(data, 0);
            if (contentInfo.Tag != 0x30)
            {
                return null;
            }

            var oid = ReadTlv(data, contentInfo.Value);
            var wrapped = ReadTlv(data, oid.End);
            if ((wrapped.Tag & 0xE0) != 0xA0)
            {
                return null;
            }

            var signedData = ReadTlv(data, wrapped.Value);
            if (signedData.Tag != 0x30)
            {
                return null;
            }

            var offset = signedData.Value;
            Tlv lastSet = default;
            var foundSet = false;
            while (offset < signedData.End)
            {
                var child = ReadTlv(data, offset);
                if (child.Tag == 0x31)
                {
                    lastSet = child;
                    foundSet = true;
                }

                offset = child.End;
            }

            if (!foundSet || lastSet.Length == 0)
            {
                return null;
            }

            var signerInfo = ReadTlv(data, lastSet.Value);
            if (signerInfo.Tag != 0x30)
            {
                return null;
            }

            offset = signerInfo.Value;
            Tlv signedAttrs = default;
            var foundAttrs = false;
            while (offset < signerInfo.End)
            {
                var child = ReadTlv(data, offset);
                if (child.Tag == 0xA0)
                {
                    signedAttrs = child;
                    foundAttrs = true;
                }

                offset = child.End;
            }

            if (!foundAttrs)
            {
                return null;
            }

            var encoded = new byte[signedAttrs.End - signedAttrs.Start];
            Buffer.BlockCopy(data, signedAttrs.Start, encoded, 0, encoded.Length);
            encoded[0] = 0x31;
            return encoded;
        }
        catch
        {
            return null;
        }
    }

    private readonly struct Tlv
    {
        public Tlv(int tag, int start, int value, int length, int end)
        {
            Tag = tag;
            Start = start;
            Value = value;
            Length = length;
            End = end;
        }

        public int Tag { get; }
        public int Start { get; }
        public int Value { get; }
        public int Length { get; }
        public int End { get; }
    }

    private static Tlv ReadTlv(byte[] data, int offset)
    {
        var start = offset;
        var tag = data[offset];
        offset++;
        if ((tag & 0x1F) == 0x1F)
        {
            while (offset < data.Length && (data[offset] & 0x80) != 0)
            {
                offset++;
            }

            offset++;
        }

        var lenByte = data[offset++];
        int length;
        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            var count = lenByte & 0x7F;
            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | data[offset++];
            }
        }

        var value = offset;
        return new Tlv(tag, start, value, length, value + length);
    }

    private static byte[]? ExtractOriginalSignedAttributes(byte[] cmsBytes)
    {
        try
        {
            var contentInfo = Asn1Sequence.GetInstance(DerUtil.Trim(cmsBytes));
            if (contentInfo.Count < 2)
            {
                return null;
            }

            var signedData = ReadExplicitSequence(contentInfo[1]);
            if (signedData is null)
            {
                return null;
            }

            Asn1Set? signerInfos = null;
            for (var i = 0; i < signedData.Count; i++)
            {
                if (signedData[i] is Asn1Set set)
                {
                    signerInfos = set;
                }
            }

            if (signerInfos is null || signerInfos.Count == 0)
            {
                return null;
            }

            var signerInfo = Asn1Sequence.GetInstance(signerInfos[0]);
            Asn1TaggedObject? signedAttrs = null;
            for (var i = 0; i < signerInfo.Count; i++)
            {
                if (signerInfo[i] is Asn1TaggedObject tagged && tagged.TagNo == 0)
                {
                    signedAttrs = tagged;
                }
            }

            if (signedAttrs is null)
            {
                return null;
            }

            var encoded = signedAttrs.GetEncoded();
            if (encoded.Length > 0 && encoded[0] == 0xA0)
            {
                encoded[0] = 0x31;
            }

            return encoded;
        }
        catch
        {
            return null;
        }
    }

    private static Asn1Sequence? ReadExplicitSequence(Asn1Encodable encoded)
    {
        try
        {
            if (encoded is Asn1Sequence sequence)
            {
                return sequence;
            }

            var tagged = Asn1TaggedObject.GetInstance(encoded);
            try
            {
                return Asn1Sequence.GetInstance(tagged.GetExplicitBaseObject());
            }
            catch
            {
                return Asn1Sequence.GetInstance(tagged.GetBaseObject());
            }
        }
        catch
        {
            return null;
        }
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

            return false;
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
            foreach (var hash in HashesOf(new Gost3411Digest(), data))
            {
                yield return hash;
            }

            foreach (var sbox in new[] { "Default", "E-A", "E-B", "E-C", "E-D" })
            {
                Org.BouncyCastle.Crypto.IDigest? digest = null;
                try
                {
                    digest = new Gost3411Digest(Gost28147Engine.GetSBox(sbox));
                }
                catch
                {
                    // unknown S-box name in this BouncyCastle build
                }

                if (digest is null)
                {
                    continue;
                }

                foreach (var hash in HashesOf(digest, data))
                {
                    yield return hash;
                }
            }

            foreach (var hash in HashesOf(new Gost3411_2012_256Digest(), data))
            {
                yield return hash;
            }
        }
        else if (size is 64)
        {
            foreach (var hash in HashesOf(new Gost3411_2012_512Digest(), data))
            {
                yield return hash;
            }
        }
        else
        {
            foreach (var hash in HashesOf(new Gost3411Digest(), data))
            {
                yield return hash;
            }
        }
    }

    private static IEnumerable<byte[]> HashesOf(Org.BouncyCastle.Crypto.IDigest digest, byte[] data)
    {
        var hash = Digest(digest, data);
        yield return hash;
        yield return Reverse(hash);
    }

    private static byte[] Reverse(byte[] data)
    {
        var copy = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            copy[i] = data[data.Length - 1 - i];
        }

        return copy;
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
            keyBytes = UnwrapKeyBytes(spki.PublicKeyData.GetOctets());
        }
        catch
        {
            try
            {
                keyBytes = UnwrapKeyBytes(spki.PublicKeyData.GetBytes());
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
            var named = ToDomain(ECGost3410NamedCurves.GetByOid(oid));
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
                named = ToDomain(ECGost3410NamedCurves.GetByOid(oid));
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

    private static ECDomainParameters? ToDomain(X9ECParameters? x9)
    {
        if (x9 is null)
        {
            return null;
        }

        return new ECDomainParameters(x9.Curve, x9.G, x9.N, x9.H, x9.GetSeed());
    }

    private static IEnumerable<DerObjectIdentifier> EnumerateParameterOids(SubjectPublicKeyInfo spki)
    {
        Asn1Encodable? parameters;
        try
        {
            parameters = spki.AlgorithmID.Parameters;
        }
        catch
        {
            yield break;
        }

        if (parameters is null)
        {
            yield break;
        }

        if (parameters is DerObjectIdentifier single)
        {
            yield return single;
            yield break;
        }

        Asn1Sequence? sequence;
        try
        {
            sequence = Asn1Sequence.GetInstance(parameters.ToAsn1Object());
        }
        catch
        {
            yield break;
        }

        if (sequence is null)
        {
            yield break;
        }

        for (var i = 0; i < sequence.Count; i++)
        {
            if (sequence[i] is DerObjectIdentifier oid)
            {
                yield return oid;
            }
        }
    }

    private static byte[] UnwrapKeyBytes(byte[] keyBytes)
    {
        if (keyBytes.Length == 66 && keyBytes[0] == 0x04 && keyBytes[1] == 0x40)
        {
            var inner = new byte[64];
            Buffer.BlockCopy(keyBytes, 2, inner, 0, 64);
            return inner;
        }

        try
        {
            var octets = Asn1OctetString.GetInstance(keyBytes).GetOctets();
            if (octets is { Length: 64 or 65 })
            {
                return octets;
            }
        }
        catch
        {
            // already a raw point encoding
        }

        return keyBytes;
    }

    private static ECPublicKeyParameters? TryEcKey(byte[] keyBytes, ECDomainParameters curve)
    {
        try
        {
            ECPoint? point = null;
            if (keyBytes.Length == 64)
            {
                point = TryCreatePoint(curve, UnsignedLe(keyBytes, 0, 32), UnsignedLe(keyBytes, 32, 32))
                        ?? TryCreatePoint(curve, UnsignedBe(keyBytes, 0, 32), UnsignedBe(keyBytes, 32, 32));
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

    private static ECPoint? TryCreatePoint(ECDomainParameters curve, BigInteger x, BigInteger y)
    {
        try
        {
            var point = curve.Curve.CreatePoint(x, y);
            return point.IsInfinity ? null : point;
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
                    var gost = new ECGost3410Signer();
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
            var verifier = new Gost3410DigestSigner(new ECGost3410Signer(), new Gost3411Digest());
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
