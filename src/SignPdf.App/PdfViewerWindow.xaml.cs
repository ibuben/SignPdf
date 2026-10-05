using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SignPdf.Pdf;

namespace SignPdf.App;

public partial class PdfViewerWindow : Window
{
    private readonly string _filePath;
    private readonly ObservableCollection<PdfPageItem> _pages = new();
    private WinPdfDocument? _document;
    private CancellationTokenSource? _renderCts;
    private double _zoom = 1;
    private bool _fitWidth = true;
    private int _renderVersion;
    private bool _layoutReady;
    private int _signatureCount;
    private ViewerStatusKind _statusKind = ViewerStatusKind.Opening;
    private string _statusArg = "";
    private PdfTextIndex? _textIndex;
    private IReadOnlyList<PdfTextHit> _hits = Array.Empty<PdfTextHit>();
    private int _hitIndex = -1;
    private bool _printing;

    public PdfViewerWindow(string filePath)
    {
        _filePath = filePath;
        InitializeComponent();
        PagesList.ItemsSource = _pages;
        ApplyLanguage();
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) =>
        {
            Loc.Instance.PropertyChanged -= OnLanguageChanged;
            _renderCts?.Cancel();
            _document?.Dispose();
        };
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => ApplyLanguage();

    private void ThemeClick(object sender, RoutedEventArgs e) => Theme.Instance.Toggle();

    private void ApplyLanguage()
    {
        Title = Loc.T("viewer_title_file", Path.GetFileName(_filePath));
        FitWidthButton.Content = Loc.T("fit_width");
        PrintButton.Content = Loc.T("print");
        SearchPlaceholder.Text = Loc.T("search_placeholder");
        UpdateSignatureButton();
        UpdateSearchCount();
        ApplyStatus();
    }

    public string FilePath => _filePath;

    public static void Open(string filePath, Window? owner)
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is PdfViewerWindow existing
                && string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                existing.Activate();
                return;
            }
        }

        var viewer = new PdfViewerWindow(filePath);
        if (owner is not null)
        {
            viewer.Owner = owner;
        }

        viewer.Show();
    }

    private async Task OpenAsync()
    {
        try
        {
            _document?.Dispose();
            _document = await WinPdfDocument.OpenAsync(_filePath);
            _pages.Clear();
            for (uint i = 0; i < _document.PageCount; i++)
            {
                var size = _document.GetPageSize(i);
                _pages.Add(new PdfPageItem
                {
                    PageIndex = i,
                    PageWidth = size.Width,
                    PageHeight = size.Height,
                    DisplayWidth = Math.Max(120, size.Width * _zoom),
                });
            }

            PageLabel.Text = Loc.T("pages", _document.PageCount);
            PrintButton.IsEnabled = _document.PageCount > 0;
            SetStatus(ViewerStatusKind.Path, _filePath);
            _layoutReady = true;
            TryApplyFitWidth();
            DetectSignatures();
            _ = BuildSearchIndexAsync();
            await RenderPagesAsync();
        }
        catch (Exception ex)
        {
            PrintButton.IsEnabled = false;
            SetStatus(ViewerStatusKind.OpenFail, ex.Message);
        }
    }

    private async Task BuildSearchIndexAsync()
    {
        try
        {
            var path = _filePath;
            var index = await Task.Run(() => PdfTextSearch.Build(path)).ConfigureAwait(true);
            _textIndex = index;
            for (var i = 0; i < _pages.Count && i < index.Pages.Count; i++)
            {
                _pages[i].SetPdfSpace(index.Pages[i].PageWidth, index.Pages[i].PageHeight);
            }

            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                RunSearch(SearchBox.Text, reset: true);
            }
        }
        catch
        {
            _textIndex = null;
        }
    }

    private void DetectSignatures()
    {
        try
        {
            _signatureCount = new PdfVerifyService().CountSignatures(_filePath);
            UpdateSignatureButton();
        }
        catch
        {
            _signatureCount = 0;
            SignatureButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RenderPagesAsync()
    {
        if (_document is null || _pages.Count == 0)
        {
            return;
        }

        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;
        var version = ++_renderVersion;
        var document = _document;
        var scale = _zoom;

        try
        {
            for (var i = 0; i < _pages.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (version != _renderVersion || !ReferenceEquals(document, _document))
                {
                    return;
                }

                var image = await document.RenderPageAsync(_pages[i].PageIndex, scale, token);
                if (version != _renderVersion)
                {
                    return;
                }

                _pages[i].Image = image;
                SetStatus(ViewerStatusKind.PageOf, null, i + 1, _pages.Count);
            }

            SetStatus(ViewerStatusKind.Path, _filePath);
        }
        catch (OperationCanceledException)
        {
            // zoom / close
        }
        catch (Exception ex)
        {
            SetStatus(ViewerStatusKind.RenderFail, ex.Message);
        }
    }

    private bool TryApplyFitWidth()
    {
        if (!_fitWidth || _pages.Count == 0 || Scroller.ViewportWidth <= 40)
        {
            UpdateZoomLabel();
            return false;
        }

        var pageWidth = _pages[0].PageWidth;
        if (pageWidth <= 1)
        {
            return false;
        }

        var next = Math.Clamp((Scroller.ViewportWidth - 56) / pageWidth, 0.4, 3);
        if (Math.Abs(next - _zoom) < 0.01)
        {
            UpdateZoomLabel();
            return false;
        }

        SetZoom(next, keepFit: true, render: false);
        return true;
    }

    private void SetZoom(double value, bool keepFit, bool render)
    {
        _fitWidth = keepFit;
        _zoom = Math.Clamp(value, 0.4, 3);
        foreach (var page in _pages)
        {
            page.DisplayWidth = Math.Max(120, page.PageWidth * _zoom);
        }

        UpdateZoomLabel();
        if (render)
        {
            _ = RenderPagesAsync();
        }
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = Math.Round(_zoom * 100) + "%";

    private void ZoomInClick(object sender, RoutedEventArgs e) => SetZoom(_zoom + 0.15, keepFit: false, render: true);

    private void ZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(_zoom - 0.15, keepFit: false, render: true);

    private void FitWidthClick(object sender, RoutedEventArgs e)
    {
        _fitWidth = true;
        TryApplyFitWidth();
        _ = RenderPagesAsync();
    }

    private void ScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layoutReady && _fitWidth && TryApplyFitWidth())
        {
            _ = RenderPagesAsync();
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), keepFit: false, render: true);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = PrintAsync();
        }
    }

    private void PrintClick(object sender, RoutedEventArgs e) => _ = PrintAsync();

    private async Task PrintAsync()
    {
        if (_document is null || _document.PageCount == 0 || _printing)
        {
            return;
        }

        var pageCount = (int)_document.PageCount;
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            MinPage = 1,
            MaxPage = _document.PageCount,
            PageRange = new PageRange(1, pageCount),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var from = 1;
        var to = pageCount;
        if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
        {
            from = Math.Clamp(dialog.PageRange.PageFrom, 1, pageCount);
            to = Math.Clamp(dialog.PageRange.PageTo, from, pageCount);
        }

        var area = PrintableSize(dialog);
        if (area.Width < 10 || area.Height < 10)
        {
            SetStatus(ViewerStatusKind.PrintFail, "");
            return;
        }

        _printing = true;
        PrintButton.IsEnabled = false;
        SetStatus(ViewerStatusKind.Printing);
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
        try
        {
            var document = _document;
            var dpi = PrinterDpi(dialog);
            var sheets = new List<(BitmapSource Image, double Width, double Height)>(to - from + 1);
            for (var number = from; number <= to; number++)
            {
                if (!IsLoaded || document is null)
                {
                    return;
                }

                var index = (uint)(number - 1);
                var size = document.GetPageSize(index);
                var fit = Math.Min(area.Width / size.Width, area.Height / size.Height);
                if (fit <= 0 || double.IsNaN(fit) || double.IsInfinity(fit))
                {
                    fit = 1;
                }

                var image = await document.RenderPageAsync(index, fit * dpi / 96.0, CancellationToken.None);
                sheets.Add((image, size.Width * fit, size.Height * fit));
            }

            if (!IsLoaded)
            {
                return;
            }

            dialog.PrintDocument(new PdfPrintPaginator(sheets, area), PdfPathNames.ForPrintJob(_filePath));
            SetStatus(ViewerStatusKind.Path, _filePath);
        }
        catch (Exception ex)
        {
            if (IsLoaded)
            {
                SetStatus(ViewerStatusKind.PrintFail, ex.Message);
            }
        }
        finally
        {
            _printing = false;
            if (IsLoaded)
            {
                PrintButton.IsEnabled = _document is not null && _document.PageCount > 0;
            }
        }
    }

    private static Size PrintableSize(PrintDialog dialog)
    {
        if (dialog.PrintableAreaWidth >= 10 && dialog.PrintableAreaHeight >= 10)
        {
            return new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        }

        try
        {
            var area = dialog.PrintQueue?.GetPrintCapabilities(dialog.PrintTicket)?.PageImageableArea;
            if (area is not null && area.ExtentWidth >= 10 && area.ExtentHeight >= 10)
            {
                return new Size(area.ExtentWidth, area.ExtentHeight);
            }
        }
        catch
        {
            // Some drivers do not report an imageable area.
        }

        return new Size(0, 0);
    }

    private static int PrinterDpi(PrintDialog dialog)
    {
        try
        {
            var resolution = dialog.PrintTicket?.PageResolution;
            var dpi = resolution?.X ?? resolution?.Y;
            if (dpi is >= 150 and <= 1200)
            {
                return dpi.Value;
            }
        }
        catch
        {
            // Some drivers omit a numeric resolution.
        }

        return 300;
    }

    private void SearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RunSearch(SearchBox.Text, reset: true);
    }

    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                MoveHit(-1);
            }
            else
            {
                MoveHit(1);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void SearchPrevClick(object sender, RoutedEventArgs e) => MoveHit(-1);

    private void SearchNextClick(object sender, RoutedEventArgs e) => MoveHit(1);

    private void RunSearch(string query, bool reset)
    {
        ClearHighlights();
        if (string.IsNullOrWhiteSpace(query) || _textIndex is null)
        {
            _hits = Array.Empty<PdfTextHit>();
            _hitIndex = -1;
            UpdateSearchCount();
            return;
        }

        _hits = PdfTextSearch.Find(_textIndex, query);
        foreach (var hit in _hits)
        {
            if (hit.PageIndex < 0 || hit.PageIndex >= _pages.Count)
            {
                continue;
            }

            _pages[hit.PageIndex].AddHighlights(hit.Rects);
        }

        _hitIndex = _hits.Count == 0 ? -1 : (reset ? 0 : Math.Clamp(_hitIndex, 0, _hits.Count - 1));
        ApplyCurrentHit(scroll: reset);
        UpdateSearchCount();
    }

    private void MoveHit(int delta)
    {
        if (_hits.Count == 0)
        {
            return;
        }

        _hitIndex = (_hitIndex + delta + _hits.Count) % _hits.Count;
        ApplyCurrentHit(scroll: true);
        UpdateSearchCount();
    }

    private void ApplyCurrentHit(bool scroll)
    {
        foreach (var item in _pages)
        {
            item.ClearCurrent();
        }

        if (_hitIndex >= 0 && _hitIndex < _hits.Count)
        {
            var hit = _hits[_hitIndex];
            if (hit.PageIndex >= 0 && hit.PageIndex < _pages.Count)
            {
                _pages[hit.PageIndex].SetCurrent(hit);
            }
        }

        if (!scroll || _hitIndex < 0 || _hitIndex >= _hits.Count)
        {
            return;
        }

        var pageIndex = _hits[_hitIndex].PageIndex;
        PagesList.UpdateLayout();
        if (PagesList.ItemContainerGenerator.ContainerFromIndex(pageIndex) is FrameworkElement page)
        {
            page.BringIntoView();
        }
    }

    private void ClearHighlights()
    {
        foreach (var page in _pages)
        {
            page.Highlights.Clear();
        }
    }

    private void UpdateSearchCount()
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchCount.Text = "";
            return;
        }

        SearchCount.Text = _hits.Count == 0
            ? Loc.T("search_none")
            : Loc.T("search_count", _hitIndex + 1, _hits.Count);
    }

    private void ShowSignatureClick(object sender, RoutedEventArgs e)
    {
        var window = new SignaturesWindow(_filePath)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private void UpdateSignatureButton()
    {
        SignatureButton.Visibility = _signatureCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_signatureCount > 0)
        {
            SignatureButton.Content = _signatureCount == 1
                ? Loc.T("show_signature")
                : Loc.T("show_signatures", _signatureCount);
        }
    }

    private void SetStatus(ViewerStatusKind kind, string? arg = null, int page = 0, int total = 0)
    {
        _statusKind = kind;
        _statusArg = arg ?? "";
        if (kind == ViewerStatusKind.PageOf)
        {
            _statusArg = page + "\u001f" + total;
        }

        ApplyStatus();
    }

    private void ApplyStatus()
    {
        StatusLabel.Text = _statusKind switch
        {
            ViewerStatusKind.Opening => Loc.T("opening"),
            ViewerStatusKind.OpenFail => Loc.T("open_fail", _statusArg),
            ViewerStatusKind.RenderFail => Loc.T("render_fail", _statusArg),
            ViewerStatusKind.Printing => Loc.T("printing"),
            ViewerStatusKind.PrintFail => string.IsNullOrEmpty(_statusArg)
                ? Loc.T("print_no_area")
                : Loc.T("print_fail", _statusArg),
            ViewerStatusKind.PageOf => SplitPageOf(_statusArg),
            _ => string.IsNullOrWhiteSpace(_statusArg) ? Loc.T("opening") : _statusArg,
        };
        if (_document is not null)
        {
            PageLabel.Text = Loc.T("pages", _document.PageCount);
        }
    }

    private static string SplitPageOf(string packed)
    {
        var parts = packed.Split('\u001f');
        return parts.Length == 2 ? Loc.T("page_of", parts[0], parts[1]) : packed;
    }

    private enum ViewerStatusKind
    {
        Opening,
        Path,
        OpenFail,
        RenderFail,
        PageOf,
        Printing,
        PrintFail,
    }
}

internal sealed class PdfPrintPaginator : DocumentPaginator, IDocumentPaginatorSource
{
    private readonly IReadOnlyList<(BitmapSource Image, double Width, double Height)> _pages;

    public PdfPrintPaginator(IReadOnlyList<(BitmapSource Image, double Width, double Height)> pages, Size pageSize)
    {
        _pages = pages;
        PageSize = pageSize;
    }

    public override bool IsPageCountValid => true;

    public override int PageCount => _pages.Count;

    public override Size PageSize { get; set; }

    public override IDocumentPaginatorSource Source => this;

    DocumentPaginator IDocumentPaginatorSource.DocumentPaginator => this;

    public override DocumentPage GetPage(int pageNumber)
    {
        var (image, width, height) = _pages[pageNumber];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var x = (PageSize.Width - width) / 2;
            var y = (PageSize.Height - height) / 2;
            dc.DrawRectangle(Brushes.White, null, new Rect(new Point(0, 0), PageSize));
            dc.DrawImage(image, new Rect(x, y, width, height));
        }

        var bounds = new Rect(new Point(0, 0), PageSize);
        return new DocumentPage(visual, PageSize, bounds, bounds);
    }
}

internal sealed class PdfPageItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private double _displayWidth;
    private double _pdfWidth;
    private double _pdfHeight;

    public uint PageIndex { get; init; }
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }
    public ObservableCollection<PdfHighlight> Highlights { get; } = new();

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public double DisplayWidth
    {
        get => _displayWidth;
        set
        {
            _displayWidth = value;
            RelayoutHighlights();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayHeight)));
        }
    }

    public double DisplayHeight => PageWidth <= 1 ? 1 : PageHeight * (_displayWidth / PageWidth);

    public void SetPdfSpace(float width, float height)
    {
        _pdfWidth = width;
        _pdfHeight = height;
        RelayoutHighlights();
    }

    public void AddHighlights(IReadOnlyList<PdfCharBox> rects)
    {
        foreach (var rect in rects)
        {
            Highlights.Add(new PdfHighlight(rect.X, rect.Y, rect.Width, rect.Height));
        }

        RelayoutHighlights();
    }

    public void ClearCurrent()
    {
        foreach (var highlight in Highlights)
        {
            highlight.SetCurrent(false);
        }
    }

    public void SetCurrent(PdfTextHit hit)
    {
        foreach (var highlight in Highlights)
        {
            var current = false;
            foreach (var rect in hit.Rects)
            {
                if (highlight.Matches(rect))
                {
                    current = true;
                    break;
                }
            }

            highlight.SetCurrent(current);
        }
    }

    private void RelayoutHighlights()
    {
        var spaceW = _pdfWidth > 1 ? _pdfWidth : PageWidth;
        var spaceH = _pdfHeight > 1 ? _pdfHeight : PageHeight;
        if (spaceW <= 1 || spaceH <= 1 || _displayWidth <= 1)
        {
            return;
        }

        foreach (var highlight in Highlights)
        {
            highlight.Layout(_displayWidth / spaceW, DisplayHeight / spaceH);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class PdfHighlight : INotifyPropertyChanged
{
    public PdfHighlight(double pdfX, double pdfY, double pdfWidth, double pdfHeight)
    {
        PdfX = pdfX;
        PdfY = pdfY;
        PdfWidth = pdfWidth;
        PdfHeight = pdfHeight;
    }

    public double PdfX { get; }
    public double PdfY { get; }
    public double PdfWidth { get; }
    public double PdfHeight { get; }
    public bool IsCurrent { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Width { get; private set; }
    public double Height { get; private set; }

    public bool Matches(PdfCharBox box) =>
        Math.Abs(PdfX - box.X) < 0.2
        && Math.Abs(PdfY - box.Y) < 0.2
        && Math.Abs(PdfWidth - box.Width) < 0.2
        && Math.Abs(PdfHeight - box.Height) < 0.2;

    public void Layout(double scaleX, double scaleY)
    {
        X = PdfX * scaleX;
        Y = PdfY * scaleY;
        Width = Math.Max(2, PdfWidth * scaleX);
        Height = Math.Max(2, PdfHeight * scaleY);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(X)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Y)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Height)));
    }

    public void SetCurrent(bool value)
    {
        if (IsCurrent == value)
        {
            return;
        }

        IsCurrent = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
