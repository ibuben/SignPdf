using System.Globalization;
using iText.IO.Font;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Xobject;
using IoPath = System.IO.Path;

namespace SignPdf.Pdf;

internal static class SignatureStampPainter
{
    private static readonly DeviceRgb NumberAccent = new(15, 108, 92);

    public static void Draw(PdfFormXObject layer, PdfDocument document, Rectangle rect, PdfStampInfo stamp, int index)
    {
        var regular = TryLoadFont("arial.ttf", "segoeui.ttf", "tahoma.ttf", "calibri.ttf")
                      ?? throw new InvalidOperationException("Не найден шрифт Arial/Segoe UI для штампа подписи.");
        var bold = TryLoadFont("arialbd.ttf", "segoeuib.ttf", "tahomabd.ttf", "calibrib.ttf") ?? regular;
        var canvas = new PdfCanvas(layer, document);
        var width = rect.GetWidth();
        var height = rect.GetHeight();

        canvas.SaveState();
        canvas.SetExtGState(new PdfExtGState().SetFillOpacity(0.48f));
        canvas.SetFillColor(NumberAccent);
        canvas.BeginText();
        canvas.SetFontAndSize(bold, 96);
        canvas.SetTextMatrix(8, 28);
        canvas.ShowText(Math.Max(1, index).ToString(CultureInfo.InvariantCulture));
        canvas.EndText();
        canvas.RestoreState();

        var lines = BuildLines(stamp);
        var y = height - 20f;
        const float leading = 20f;
        const float x = 14f;
        const float fontSize = 13f;

        foreach (var line in lines)
        {
            canvas.BeginText();
            canvas.SetFontAndSize(regular, fontSize);
            canvas.SetTextMatrix(x, y);
            canvas.SetFillColor(ColorConstants.BLACK);
            canvas.ShowText(line.Label);
            var labelWidth = regular.GetWidth(line.Label, fontSize);
            canvas.SetFillColor(ColorConstants.BLACK);
            canvas.SetTextMatrix(x + labelWidth, y);
            var value = Fit(regular, line.Value, fontSize, width - x - labelWidth - 6);
            if (value.Length > 0)
            {
                canvas.ShowText(value);
            }

            canvas.EndText();
            y -= leading;
        }
    }

    private static (string Label, string Value)[] BuildLines(PdfStampInfo stamp)
    {
        var date = stamp.SignedAt.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
        var idLabel = string.IsNullOrWhiteSpace(stamp.IdLabel) ? "инн" : stamp.IdLabel;
        return
        [
            ((stamp.DateLabel ?? "дата") + ": ", date),
            ((stamp.SerialLabel ?? "серийный номер") + ": ", ShortSerial(stamp.SerialNumber)),
            ((stamp.CompanyLabel ?? "компания") + ": ", (stamp.Company ?? "").Trim().ToUpperInvariant()),
            (idLabel + ": ", stamp.IdValue ?? ""),
            ((stamp.NameLabel ?? "фио") + ": ", stamp.FullName ?? ""),
        ];
    }

    private static string ShortSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length >= 8)
        {
            return hex[^8..].ToLowerInvariant();
        }

        return raw.Trim();
    }

    private static string Fit(PdfFont font, string text, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 8)
        {
            return text;
        }

        if (font.GetWidth(text, fontSize) <= maxWidth)
        {
            return text;
        }

        var ellipsis = "…";
        for (var i = text.Length - 1; i > 0; i--)
        {
            var cut = text[..i] + ellipsis;
            if (font.GetWidth(cut, fontSize) <= maxWidth)
            {
                return cut;
            }
        }

        return ellipsis;
    }

    private static PdfFont? TryLoadFont(params string[] fileNames)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var fileName in fileNames)
        {
            var path = IoPath.Combine(fonts, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            return PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
        }

        return null;
    }
}

public sealed class PdfStampInfo
{
    public DateTime SignedAt { get; set; } = DateTime.Now;
    public string SerialNumber { get; set; } = "";
    public string Company { get; set; } = "";
    public string DateLabel { get; set; } = "дата";
    public string SerialLabel { get; set; } = "серийный номер";
    public string CompanyLabel { get; set; } = "компания";
    public string NameLabel { get; set; } = "фио";
    public string IdLabel { get; set; } = "инн";
    public string IdValue { get; set; } = "";
    public string FullName { get; set; } = "";
}
