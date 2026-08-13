using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace SignPdf.Pdf;

public sealed class PdfTextIndex
{
    public IReadOnlyList<PdfPageText> Pages { get; init; } = Array.Empty<PdfPageText>();
}

public sealed class PdfPageText
{
    public int PageIndex { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<PdfCharBox> Chars { get; init; } = Array.Empty<PdfCharBox>();
    public float PageWidth { get; init; }
    public float PageHeight { get; init; }
}

public readonly struct PdfCharBox
{
    public PdfCharBox(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public float X { get; }
    public float Y { get; }
    public float Width { get; }
    public float Height { get; }
}

public sealed class PdfTextHit
{
    public int PageIndex { get; init; }
    public IReadOnlyList<PdfCharBox> Rects { get; init; } = Array.Empty<PdfCharBox>();
}

public static class PdfTextSearch
{
    public static PdfTextIndex Build(string filePath)
    {
        using var reader = new PdfReader(filePath);
        using var pdf = new PdfDocument(reader);
        var pages = new List<PdfPageText>(pdf.GetNumberOfPages());
        for (var i = 1; i <= pdf.GetNumberOfPages(); i++)
        {
            pages.Add(ExtractPage(pdf.GetPage(i), i - 1));
        }

        return new PdfTextIndex { Pages = pages };
    }

    public static IReadOnlyList<PdfTextHit> Find(PdfTextIndex index, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || index.Pages.Count == 0)
        {
            return Array.Empty<PdfTextHit>();
        }

        var needle = query.Trim();
        var hits = new List<PdfTextHit>();
        foreach (var page in index.Pages)
        {
            var start = 0;
            while (start < page.Text.Length)
            {
                var at = page.Text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                hits.Add(new PdfTextHit
                {
                    PageIndex = page.PageIndex,
                    Rects = MergeRects(page.Chars, at, needle.Length),
                });
                start = at + Math.Max(1, needle.Length);
            }
        }

        return hits;
    }

    private static PdfPageText ExtractPage(PdfPage page, int pageIndex)
    {
        var crop = page.GetCropBox() ?? page.GetPageSize();
        var rotation = ((page.GetRotation() % 360) + 360) % 360;
        var visual = VisualSize(crop, rotation);
        var collector = new CharCollector(crop, rotation, visual);
        new PdfCanvasProcessor(collector).ProcessPageContent(page);
        return new PdfPageText
        {
            PageIndex = pageIndex,
            Text = collector.Text.ToString(),
            Chars = collector.Chars,
            PageWidth = visual.Width,
            PageHeight = visual.Height,
        };
    }

    private static (float Width, float Height) VisualSize(Rectangle crop, int rotation) =>
        rotation is 90 or 270
            ? (crop.GetHeight(), crop.GetWidth())
            : (crop.GetWidth(), crop.GetHeight());

    private static IReadOnlyList<PdfCharBox> MergeRects(IReadOnlyList<PdfCharBox> chars, int start, int length)
    {
        var end = Math.Min(chars.Count, start + length);
        if (start < 0 || start >= chars.Count || end <= start)
        {
            return Array.Empty<PdfCharBox>();
        }

        var rects = new List<PdfCharBox>();
        var current = chars[start];
        for (var i = start + 1; i < end; i++)
        {
            var next = chars[i];
            var sameLine = Math.Abs(next.Y - current.Y) <= Math.Max(2, current.Height * 0.4f)
                           && Math.Abs(next.Height - current.Height) <= Math.Max(2, current.Height * 0.4f);
            var adjacent = next.X <= current.X + current.Width + Math.Max(4, current.Height);
            if (sameLine && adjacent && next.X >= current.X - 1)
            {
                var right = Math.Max(current.X + current.Width, next.X + next.Width);
                var bottom = Math.Max(current.Y + current.Height, next.Y + next.Height);
                var x = Math.Min(current.X, next.X);
                var y = Math.Min(current.Y, next.Y);
                current = new PdfCharBox(x, y, right - x, bottom - y);
            }
            else
            {
                rects.Add(Inflate(current));
                current = next;
            }
        }

        rects.Add(Inflate(current));
        return rects;
    }

    private static PdfCharBox Inflate(PdfCharBox box)
    {
        const float pad = 0.8f;
        return new PdfCharBox(
            box.X - pad,
            box.Y - pad,
            Math.Max(2, box.Width + pad * 2),
            Math.Max(2, box.Height + pad * 2));
    }

    private sealed class CharCollector : IEventListener
    {
        private readonly Rectangle _crop;
        private readonly int _rotation;
        private readonly float _visualHeight;

        public CharCollector(Rectangle crop, int rotation, (float Width, float Height) visual)
        {
            _crop = crop;
            _rotation = rotation;
            _visualHeight = visual.Height;
        }

        public StringBuilder Text { get; } = new();
        public List<PdfCharBox> Chars { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo info)
            {
                return;
            }

            foreach (var glyph in info.GetCharacterRenderInfos())
            {
                var value = glyph.GetText();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var box = MapBox(glyph);
                foreach (var ch in value)
                {
                    Text.Append(ch);
                    Chars.Add(box);
                }
            }
        }

        public ICollection<EventType> GetSupportedEvents() => new HashSet<EventType> { EventType.RENDER_TEXT };

        private PdfCharBox MapBox(TextRenderInfo info)
        {
            var descent = info.GetDescentLine();
            var ascent = info.GetAscentLine();
            var xs = new[]
            {
                descent.GetStartPoint().Get(0),
                descent.GetEndPoint().Get(0),
                ascent.GetStartPoint().Get(0),
                ascent.GetEndPoint().Get(0),
            };
            var ys = new[]
            {
                descent.GetStartPoint().Get(1),
                descent.GetEndPoint().Get(1),
                ascent.GetStartPoint().Get(1),
                ascent.GetEndPoint().Get(1),
            };
            var left = xs.Min();
            var right = xs.Max();
            var bottom = ys.Min();
            var top = ys.Max();
            return ToTopLeft(
                left,
                bottom,
                Math.Max(0.5f, right - left),
                Math.Max(0.5f, top - bottom));
        }

        private PdfCharBox ToTopLeft(float x, float y, float w, float h)
        {
            var lx = x - _crop.GetX();
            var by = y - _crop.GetY();
            var cropW = _crop.GetWidth();
            var cropH = _crop.GetHeight();
            float vx, vBottom, vw, vh;
            switch (_rotation)
            {
                case 90:
                    vx = by;
                    vBottom = cropW - lx - w;
                    vw = h;
                    vh = w;
                    break;
                case 180:
                    vx = cropW - lx - w;
                    vBottom = cropH - by - h;
                    vw = w;
                    vh = h;
                    break;
                case 270:
                    vx = cropH - by - h;
                    vBottom = lx;
                    vw = h;
                    vh = w;
                    break;
                default:
                    vx = lx;
                    vBottom = by;
                    vw = w;
                    vh = h;
                    break;
            }

            return new PdfCharBox(vx, _visualHeight - vBottom - vh, vw, vh);
        }
    }
}
