namespace SignPdf.Pdf;

public interface IPdfCmsVerifier
{
    Task<CmsVerifyResult?> VerifyDetachedAsync(byte[] data, byte[] pkcs7, CancellationToken cancellationToken);
}

public sealed class CmsVerifyResult
{
    public bool? SignatureValid { get; init; }
    public bool CertificateTrusted { get; init; }
    public string TrustDetails { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public DateTime? SignedAt { get; init; }
    public DateTime? NotBefore { get; init; }
    public DateTime? NotAfter { get; init; }
    public string Algorithm { get; init; } = "";
    public string Source { get; init; } = "";
    public string Details { get; init; } = "";
    public string Reason { get; init; } = "";
}
