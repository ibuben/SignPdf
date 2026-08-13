namespace SignPdf.Pdf;

public interface IPdfCmsSigner
{
    Task<byte[]> CreateDetachedPkcs7Async(byte[] dataToSign, CancellationToken cancellationToken);
}
