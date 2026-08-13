using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;
using IoPath = System.IO.Path;

namespace SignPdf.Pdf;

public sealed class PdfSignService
{
    private static readonly int[] CmsSizeAttempts = { 32_768, 65_536, 131_072, 262_144 };

    static PdfSignService()
    {
        _ = iText.Bouncycastleconnector.BouncyCastleFactoryCreator.GetFactory();
    }

    public async Task SignAsync(
        string inputPath,
        string outputPath,
        IPdfCmsSigner cmsSigner,
        PdfSignOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(cmsSigner);
        options ??= new PdfSignOptions();

        Directory.CreateDirectory(IoPath.GetDirectoryName(IoPath.GetFullPath(outputPath))!);

        Exception? last = null;
        foreach (var estimatedSize in CmsSizeAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPath = outputPath + $".tmp-{estimatedSize}";
            try
            {
                await SignOnceAsync(inputPath, tempPath, cmsSigner, options, estimatedSize, cancellationToken)
                    .ConfigureAwait(false);
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                File.Move(tempPath, outputPath);
                return;
            }
            catch (Exception ex) when (IsTooSmall(ex))
            {
                last = ex;
                TryDelete(tempPath);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        throw last ?? new InvalidOperationException("Не удалось встроить подпись в PDF: недостаточно места под CMS.");
    }

    public static string SuggestOutputPath(string inputPath)
    {
        var dir = IoPath.GetDirectoryName(inputPath) ?? "";
        var name = IoPath.GetFileNameWithoutExtension(inputPath);
        var ext = IoPath.GetExtension(inputPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".pdf";
        }

        var candidate = IoPath.Combine(dir, $"{name}_signed{ext}");
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = IoPath.Combine(dir, $"{name}_signed_{index}{ext}");
            index++;
        }

        return candidate;
    }

    private static async Task SignOnceAsync(
        string inputPath,
        string outputPath,
        IPdfCmsSigner cmsSigner,
        PdfSignOptions options,
        int estimatedSize,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var reader = new PdfReader(inputPath);
            using var output = File.Create(outputPath);
            var stamper = new StampingProperties().UseAppendMode();
            var signer = new PdfSigner(reader, output, stamper);

            var (fieldName, existingCount) = NextSignatureSlot(inputPath, options.FieldName);
            signer.SetFieldName(fieldName);
            signer.SetReason(options.Reason);
            signer.SetLocation(options.Location);
            signer.SetSignDate(DateTime.Now);

            if (options.VisibleStamp)
            {
                using var probe = new PdfDocument(new PdfReader(inputPath));
                var page = probe.GetNumberOfPages();
                var box = probe.GetPage(page).GetPageSize();
                var rect = StampRect(box, existingCount);
                signer.SetPageNumber(page);
                signer.SetPageRect(rect);
#pragma warning disable CS0612, CS0618
                var appearance = signer.GetSignatureAppearance();
                appearance.SetReuseAppearance(false);
                var layer = appearance.GetLayer2();
#pragma warning restore CS0612, CS0618
                var stamp = options.Stamp ?? new PdfStampInfo { SignedAt = DateTime.Now };
                SignatureStampPainter.Draw(layer, signer.GetDocument(), rect, stamp, existingCount + 1);
            }

            var container = new EimzoExternalSignatureContainer(cmsSigner, cancellationToken);
            signer.SignExternalContainer(container, estimatedSize);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (string FieldName, int ExistingCount) NextSignatureSlot(string inputPath, string? preferred)
    {
        using var reader = new PdfReader(inputPath);
        using var pdf = new PdfDocument(reader);
        var names = new SignatureUtil(pdf).GetSignatureNames();
        var used = new HashSet<string>(names, StringComparer.Ordinal);
        var baseName = string.IsNullOrWhiteSpace(preferred) ? "EImzoSignature" : preferred.Trim();
        if (!used.Contains(baseName))
        {
            return (baseName, names.Count);
        }

        var index = 2;
        while (used.Contains(baseName + "_" + index))
        {
            index++;
        }

        return (baseName + "_" + index, names.Count);
    }

    private static Rectangle StampRect(Rectangle page, int existingCount)
    {
        const float width = 420f;
        const float height = 140f;
        const float margin = 36f;
        const float gap = 10f;
        var col = 0;
        var row = 0;
        for (var i = 0; i < existingCount; i++)
        {
            col++;
            if (margin + (col + 1) * width + col * gap > page.GetWidth() - margin)
            {
                col = 0;
                row++;
            }
        }

        var left = page.GetLeft() + margin + col * (width + gap);
        var bottom = page.GetBottom() + margin + row * (height + gap);
        return new Rectangle(left, bottom, width, height);
    }

    private static bool IsTooSmall(Exception ex)
    {
        var text = ex.Message ?? "";
        return text.Contains("space", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Not enough", StringComparison.OrdinalIgnoreCase)
               || text.Contains("estimated", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }
}

public sealed class PdfSignOptions
{
    public string Reason { get; set; } = "Подпись документа";
    public string Location { get; set; } = "O'zbekiston";
    public string FieldName { get; set; } = "EImzoSignature";
    public bool VisibleStamp { get; set; } = true;
    public PdfStampInfo? Stamp { get; set; }
}

internal sealed class EimzoExternalSignatureContainer : IExternalSignatureContainer
{
    private readonly IPdfCmsSigner _cmsSigner;
    private readonly CancellationToken _cancellationToken;

    public EimzoExternalSignatureContainer(IPdfCmsSigner cmsSigner, CancellationToken cancellationToken)
    {
        _cmsSigner = cmsSigner;
        _cancellationToken = cancellationToken;
    }

    public byte[] Sign(Stream data)
    {
        using var copy = new MemoryStream();
        data.CopyTo(copy);
        return _cmsSigner.CreateDetachedPkcs7Async(copy.ToArray(), _cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    public void ModifySigningDictionary(PdfDictionary signDic)
    {
        signDic.Put(PdfName.Filter, PdfName.Adobe_PPKLite);
        signDic.Put(PdfName.SubFilter, PdfName.Adbe_pkcs7_detached);
    }
}
