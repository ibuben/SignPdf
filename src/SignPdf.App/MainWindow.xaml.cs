using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using SignPdf.Pdf;

namespace SignPdf.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            FitToContentHeight();
            await vm.InitializeAsync();
        };
    }

    private void CreditLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true,
        });
        e.Handled = true;
    }

    private void ThemeClick(object sender, RoutedEventArgs e) => Theme.Instance.Toggle();

    private void SignPdfDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var allow = FindDroppedPdf(e.Data) is not null
            && DataContext is MainViewModel { IsBusy: false };
        e.Effects = allow ? DragDropEffects.Copy : DragDropEffects.None;
        SignDropOverlay.Visibility = allow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SignPdfDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        var p = e.GetPosition(fe);
        if (p.X < 0 || p.Y < 0 || p.X > fe.ActualWidth || p.Y > fe.ActualHeight)
        {
            SignDropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SignPdfDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SignDropOverlay.Visibility = Visibility.Collapsed;
        if (DataContext is not MainViewModel { IsBusy: false } vm)
        {
            return;
        }

        var path = FindDroppedPdf(e.Data);
        if (path is not null)
        {
            vm.PdfPath = path;
        }
    }

    private static string? FindDroppedPdf(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return null;
        }

        return files.FirstOrDefault(file =>
            file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(file));
    }

    private void VerificationRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid
            || ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is not DataGridRow row
            || row.Item is not SignatureCheck check)
        {
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.ShowDetailsCommand.Execute(check);
        }
    }

    private void FitToContentHeight()
    {
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        var needed = ActualHeight + 36;
        SizeToContent = SizeToContent.Manual;
        var work = SystemParameters.WorkArea.Height;
        Height = Math.Min(needed, work);
        MinHeight = Math.Min(Height, work);
        if (Top + Height > SystemParameters.WorkArea.Bottom)
        {
            Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height);
        }
    }
}
