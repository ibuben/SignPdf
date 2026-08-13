namespace SignPdf.Pdf;

public enum SignatureStatus
{
    Valid,
    DocumentModified,
    CertificateExpired,
    CertificateNotYetValid,
    SubsequentChanges,
    AlgorithmUnsupported,
    KeyNotConfirmed,
    NotConfirmed,
    Invalid,
    Error,
}

public sealed class SignatureCheck
{
    public string FieldName { get; init; } = "";
    public SignatureStatus Status { get; init; }
    public string StatusText { get; init; } = "";
    public string SignerName { get; init; } = "";
    public string Issuer { get; init; } = "";
    public DateTime? SignedAt { get; init; }
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public bool CoversWholeDocument { get; init; }
    public bool FollowedByLaterSignature { get; init; }
    public string Algorithm { get; init; } = "";
    public string Details { get; init; } = "";
    public bool IsOk => Status is SignatureStatus.Valid or SignatureStatus.SubsequentChanges;
}

public sealed class PdfVerificationResult
{
    public string FilePath { get; init; } = "";
    public int SignatureCount { get; init; }
    public IReadOnlyList<SignatureCheck> Signatures { get; init; } = Array.Empty<SignatureCheck>();
    public string Summary { get; init; } = "";
}
