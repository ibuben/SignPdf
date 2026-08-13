using SignPdf.Eimzo;
using SignPdf.Pdf;

namespace SignPdf.App;

internal sealed class EimzoCmsSigner : IPdfCmsSigner
{
    private readonly EimzoClient _client;
    private readonly string _keyId;

    public EimzoCmsSigner(EimzoClient client, string keyId)
    {
        _client = client;
        _keyId = keyId;
    }

    public async Task<byte[]> CreateDetachedPkcs7Async(byte[] dataToSign, CancellationToken cancellationToken)
    {
        var pkcs7 = await _client.CreateDetachedPkcs7Async(_keyId, dataToSign, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var chain = await _client.GetCertificateChainAsync(_keyId, cancellationToken)
                .ConfigureAwait(false);
            return chain.Count == 0 ? pkcs7 : CmsCertChain.MergeCertificates(pkcs7, chain);
        }
        catch
        {
            return pkcs7;
        }
    }
}
