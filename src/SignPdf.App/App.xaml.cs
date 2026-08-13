using System.Windows;

namespace SignPdf.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Loc.Instance.Load();
        Theme.Instance.Load();
        PdfFileAssociation.Register();
        Loc.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Loc.Language) or "")
            {
                PdfFileAssociation.Register();
            }
        };
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        var pdf = PdfFileAssociation.FindPdfPath(e.Args);
        if (pdf is not null)
        {
            var viewer = new PdfViewerWindow(pdf);
            MainWindow = viewer;
            viewer.Show();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
