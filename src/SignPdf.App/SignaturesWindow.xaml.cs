using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SignPdf.Eimzo;
using SignPdf.Pdf;

namespace SignPdf.App;

public partial class SignaturesWindow : Window
{
    public SignaturesWindow(string filePath)
    {
        InitializeComponent();
        var model = new SignaturesViewModel();
        DataContext = model;
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.Instance.PropertyChanged -= OnLanguageChanged;
        Loaded += async (_, _) => await model.LoadAsync(filePath);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Title = Loc.T("signatures_title");
        if (DataContext is SignaturesViewModel model)
        {
            model.RefreshLanguage();
        }
    }

    private void SignatureDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SignatureCheck check)
        {
            new DetailsWindow(check) { Owner = this }.ShowDialog();
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}

internal sealed class SignaturesViewModel : ObservableObject
{
    private string _summary = Loc.T("checking");
    private bool _hasResults;
    private bool _allOk;
    private bool _summaryFromLoc;

    public ObservableCollection<SignatureCheck> Signatures { get; } = new();

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool HasResults
    {
        get => _hasResults;
        private set => SetProperty(ref _hasResults, value);
    }

    public bool AllOk
    {
        get => _allOk;
        private set => SetProperty(ref _allOk, value);
    }

    public async Task LoadAsync(string filePath)
    {
        using var eimzo = new EimzoClient();
        try
        {
            IPdfCmsVerifier? verifier;
            try
            {
                await eimzo.ConnectAsync();
                verifier = new EimzoCmsVerifier(eimzo);
            }
            catch (Exception ex) when (ex is EimzoException or EimzoNotRunningException)
            {
                _summaryFromLoc = false;
                Summary = Loc.FromException(ex);
                HasResults = true;
                AllOk = false;
                return;
            }

            var result = await new PdfVerifyService().VerifyAsync(filePath, verifier);
            Signatures.Clear();
            foreach (var item in result.Signatures)
            {
                Signatures.Add(item);
            }

            _summaryFromLoc = true;
            Summary = Loc.VerifySummary(Signatures);
            HasResults = true;
            AllOk = result.Signatures.Count > 0 && result.Signatures.All(s => s.IsOk);
        }
        catch (Exception ex)
        {
            _summaryFromLoc = false;
            Summary = Loc.FromException(ex);
            HasResults = true;
            AllOk = false;
        }
    }

    public void RefreshLanguage()
    {
        if (_summaryFromLoc)
        {
            Summary = Loc.VerifySummary(Signatures);
        }
        else if (!HasResults)
        {
            Summary = Loc.T("checking");
        }
    }
}
