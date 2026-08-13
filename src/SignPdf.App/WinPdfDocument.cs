using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace SignPdf.App;

internal sealed class WinPdfDocument : IDisposable
{
    private readonly InMemoryRandomAccessStream _source;
    private readonly PdfDocument _document;
    private bool _disposed;

    private WinPdfDocument(InMemoryRandomAccessStream source, PdfDocument document)
    {
        _source = source;
        _document = document;
    }

    public uint PageCount => _document.PageCount;

    public static async Task<WinPdfDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
        var source = new InMemoryRandomAccessStream();
        var writer = new DataWriter(source);
        writer.WriteBytes(bytes);
        await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(true);
        writer.DetachStream();
        writer.Dispose();
        source.Seek(0);
        var document = await PdfDocument.LoadFromStreamAsync(source).AsTask(cancellationToken).ConfigureAwait(true);
        return new WinPdfDocument(source, document);
    }

    public Windows.Foundation.Size GetPageSize(uint index)
    {
        using var page = _document.GetPage(index);
        return page.Size;
    }

    public async Task<BitmapSource> RenderPageAsync(uint index, double scale, CancellationToken cancellationToken)
    {
        using var page = _document.GetPage(index);
        var width = Math.Max(1, (uint)Math.Round(page.Size.Width * scale));
        var height = Math.Max(1, (uint)Math.Round(page.Size.Height * scale));
        const uint maxEdge = 4096;
        if (width > maxEdge || height > maxEdge)
        {
            var fit = Math.Min((double)maxEdge / width, (double)maxEdge / height);
            width = Math.Max(1, (uint)Math.Round(width * fit));
            height = Math.Max(1, (uint)Math.Round(height * fit));
        }

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height,
        };

        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options).AsTask(cancellationToken).ConfigureAwait(true);

        using var reader = new DataReader(output.GetInputStreamAt(0));
        var size = (uint)output.Size;
        await reader.LoadAsync(size).AsTask(cancellationToken).ConfigureAwait(true);
        var png = new byte[size];
        reader.ReadBytes(png);

        var bitmap = new BitmapImage();
        using (var mem = new MemoryStream(png, writable: false))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = mem;
            bitmap.EndInit();
        }

        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.Dispose();
    }
}
