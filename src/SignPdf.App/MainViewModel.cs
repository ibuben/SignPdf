using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SignPdf.Eimzo;
using SignPdf.Pdf;

namespace SignPdf.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly EimzoClient _eimzo = new();
    private readonly PdfSignService _signService = new();
    private readonly PdfVerifyService _verifyService = new();

    private string _eimzoStatus = Loc.T("eimzo_offline");
    private bool _eimzoConnected;
    private string _eimzoVersion = "";
    private string _rawVersion = "";
    private EimzoUiKind _eimzoUi = EimzoUiKind.Offline;
    private bool _verifySummaryFromLoc;
    private EimzoCertificate? _selectedCertificate;
    private string _pdfPath = "";
    private string _outputPath = "";
    private bool _showStamp = true;
    private bool _isBusy;
    private string _signMessage = "";
    private string _verifyPdfPath = "";
    private string _verifySummary = "";
    private bool _hasVerificationResults;
    private bool _verifyAllOk;
    private SignatureCheck? _selectedVerification;

    public MainViewModel()
    {
        Certificates = new ObservableCollection<EimzoCertificate>();
        VerificationResults = new ObservableCollection<SignatureCheck>();

        ConnectCommand = new RelayCommand(ConnectAsync, () => !IsBusy);
        BrowsePdfCommand = new RelayCommand(BrowsePdfAsync, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutputAsync, () => !IsBusy);
        PreviewSignCommand = new RelayCommand(PreviewSignAsync, CanPreviewSign);
        SignCommand = new RelayCommand(SignAsync, CanSign);
        BrowseVerifyCommand = new RelayCommand(BrowseVerifyAsync, () => !IsBusy);
        PreviewVerifyCommand = new RelayCommand(PreviewVerifyAsync, CanPreviewVerify);
        VerifyCommand = new RelayCommand(VerifyAsync, CanVerify);
        AssociatePdfCommand = new RelayCommand(AssociatePdfAsync);
        CopyDetailsCommand = new RelayCommand(CopyDetailsAsync, CanCopyDetails);
        ShowDetailsCommand = new RelayCommand(ShowDetailsAsync);
        Loc.Instance.PropertyChanged += (_, _) => RefreshLanguage();
    }

    public ObservableCollection<EimzoCertificate> Certificates { get; }
    public ObservableCollection<SignatureCheck> VerificationResults { get; }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand BrowsePdfCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand PreviewSignCommand { get; }
    public RelayCommand SignCommand { get; }
    public RelayCommand BrowseVerifyCommand { get; }
    public RelayCommand PreviewVerifyCommand { get; }
    public RelayCommand VerifyCommand { get; }
    public RelayCommand AssociatePdfCommand { get; }
    public RelayCommand CopyDetailsCommand { get; }
    public RelayCommand ShowDetailsCommand { get; }

    public string EimzoStatus
    {
        get => _eimzoStatus;
        private set => SetProperty(ref _eimzoStatus, value);
    }

    public bool EimzoConnected
    {
        get => _eimzoConnected;
        private set
        {
            if (SetProperty(ref _eimzoConnected, value))
            {
                RaiseCommands();
            }
        }
    }

    public string EimzoVersion
    {
        get => _eimzoVersion;
        private set => SetProperty(ref _eimzoVersion, value);
    }

    public EimzoCertificate? SelectedCertificate
    {
        get => _selectedCertificate;
        set
        {
            if (SetProperty(ref _selectedCertificate, value))
            {
                RaiseCommands();
            }
        }
    }

    public string PdfPath
    {
        get => _pdfPath;
        set
        {
            if (SetProperty(ref _pdfPath, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                {
                    OutputPath = PdfSignService.SuggestOutputPath(value);
                }

                RaiseCommands();
            }
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public bool ShowStamp
    {
        get => _showStamp;
        set => SetProperty(ref _showStamp, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public string SignMessage
    {
        get => _signMessage;
        private set => SetProperty(ref _signMessage, value);
    }

    public string VerifyPdfPath
    {
        get => _verifyPdfPath;
        set
        {
            if (SetProperty(ref _verifyPdfPath, value))
            {
                RaiseCommands();
            }
        }
    }

    public string VerifySummary
    {
        get => _verifySummary;
        private set => SetProperty(ref _verifySummary, value);
    }

    public bool HasVerificationResults
    {
        get => _hasVerificationResults;
        private set => SetProperty(ref _hasVerificationResults, value);
    }

    public bool VerifyAllOk
    {
        get => _verifyAllOk;
        private set => SetProperty(ref _verifyAllOk, value);
    }

    public SignatureCheck? SelectedVerification
    {
        get => _selectedVerification;
        set
        {
            if (SetProperty(ref _selectedVerification, value))
            {
                CopyDetailsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync()
    {
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        SignMessage = "";
        try
        {
            SetEimzoStatus(EimzoUiKind.Connecting);
            var version = await _eimzo.ConnectAsync();
            _rawVersion = version;
            EimzoVersion = Loc.T("version", version);
            var certs = await _eimzo.ListCertificatesAsync();
            Certificates.Clear();
            foreach (var cert in certs)
            {
                Certificates.Add(cert);
            }

            SelectedCertificate = Certificates.FirstOrDefault();
            EimzoConnected = true;
            SetEimzoStatus(Certificates.Count == 0 ? EimzoUiKind.NoKeys : EimzoUiKind.RunningKeys);
        }
        catch (Exception ex)
        {
            EimzoConnected = false;
            EimzoVersion = "";
            _rawVersion = "";
            Certificates.Clear();
            SelectedCertificate = null;
            SetEimzoStatus(EimzoUiKind.Error, Loc.FromException(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task BrowsePdfAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Loc.T("filter_pdf"),
            Title = Loc.T("pick_sign"),
        };
        if (dialog.ShowDialog() == true)
        {
            PdfPath = dialog.FileName;
        }

        return Task.CompletedTask;
    }

    private Task BrowseOutputAsync()
    {
        var suggested = OutputPath;
        if (string.IsNullOrWhiteSpace(suggested) && !string.IsNullOrWhiteSpace(PdfPath))
        {
            suggested = PdfSignService.SuggestOutputPath(PdfPath);
        }

        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("filter_pdf"),
            Title = Loc.T("pick_output"),
            AddExtension = true,
            DefaultExt = ".pdf",
            OverwritePrompt = true,
        };

        if (!string.IsNullOrWhiteSpace(suggested))
        {
            dialog.FileName = Path.GetFileName(suggested);
            var dir = Path.GetDirectoryName(suggested);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                dialog.InitialDirectory = dir;
            }
        }

        if (dialog.ShowDialog() == true)
        {
            OutputPath = dialog.FileName;
        }

        return Task.CompletedTask;
    }

    private Task BrowseVerifyAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Loc.T("filter_pdf"),
            Title = Loc.T("pick_verify"),
        };
        if (dialog.ShowDialog() == true)
        {
            VerifyPdfPath = dialog.FileName;
        }

        return Task.CompletedTask;
    }

    private bool CanPreviewSign() =>
        !string.IsNullOrWhiteSpace(PdfPath) && File.Exists(PdfPath);

    private bool CanPreviewVerify() =>
        !string.IsNullOrWhiteSpace(VerifyPdfPath) && File.Exists(VerifyPdfPath);

    private Task PreviewSignAsync()
    {
        PdfViewerWindow.Open(PdfPath, Application.Current.MainWindow);
        return Task.CompletedTask;
    }

    private Task PreviewVerifyAsync()
    {
        PdfViewerWindow.Open(VerifyPdfPath, Application.Current.MainWindow);
        return Task.CompletedTask;
    }

    private static Task AssociatePdfAsync()
    {
        PdfFileAssociation.OfferAsDefault();
        return Task.CompletedTask;
    }

    private bool CanSign() =>
        !IsBusy && EimzoConnected && SelectedCertificate is not null
        && !string.IsNullOrWhiteSpace(PdfPath) && File.Exists(PdfPath);

    private bool CanVerify() =>
        !IsBusy && !string.IsNullOrWhiteSpace(VerifyPdfPath) && File.Exists(VerifyPdfPath);

    private async Task SignAsync()
    {
        if (SelectedCertificate is null)
        {
            return;
        }

        IsBusy = true;
        SignMessage = Loc.T("loading_key");
        string? keyId = null;
        try
        {
            if (!File.Exists(PdfPath))
            {
                throw new FileNotFoundException(Loc.T("pdf_missing"), PdfPath);
            }

            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                OutputPath = PdfSignService.SuggestOutputPath(PdfPath);
            }

            keyId = await _eimzo.LoadKeyAsync(SelectedCertificate);
            SignMessage = Loc.T("signing");

            var signedAt = DateTime.Now;
            await _signService.SignAsync(
                PdfPath,
                OutputPath,
                new EimzoCmsSigner(_eimzo, keyId),
                new PdfSignOptions
                {
                    VisibleStamp = ShowStamp,
                    Stamp = new PdfStampInfo
                    {
                        SignedAt = signedAt,
                        SerialNumber = SelectedCertificate.SerialNumber,
                        Company = SelectedCertificate.Organization,
                        DateLabel = Loc.T("stamp_date"),
                        SerialLabel = Loc.T("stamp_serial"),
                        CompanyLabel = Loc.T("stamp_company"),
                        NameLabel = Loc.T("stamp_name"),
                        IdLabel = StampIdLabel(SelectedCertificate),
                        IdValue = SelectedCertificate.StampIdValue,
                        FullName = SelectedCertificate.StampFullName,
                    },
                    Reason = Loc.T("reason"),
                    Location = "O'zbekiston",
                });

            SignMessage = Loc.T("signed_ok", OutputPath);
        }
        catch (Exception ex)
        {
            SignMessage = Loc.FromException(ex);
        }
        finally
        {
            if (keyId is not null)
            {
                try
                {
                    await _eimzo.UnloadKeyAsync(keyId);
                }
                catch
                {
                    // ignored
                }
            }

            IsBusy = false;
        }
    }

    private async Task VerifyAsync()
    {
        IsBusy = true;
        VerifySummary = "";
        VerificationResults.Clear();
        SelectedVerification = null;
        HasVerificationResults = false;
        VerifyAllOk = false;
        try
        {
            IPdfCmsVerifier? verifier;
            try
            {
                if (!EimzoConnected)
                {
                    var version = await _eimzo.ConnectAsync();
                    _rawVersion = version;
                    EimzoVersion = Loc.T("version", version);
                    EimzoConnected = true;
                    SetEimzoStatus(EimzoUiKind.Running);
                }

                verifier = new EimzoCmsVerifier(_eimzo);
            }
            catch (EimzoException ex)
            {
                EimzoConnected = false;
                SetEimzoStatus(EimzoUiKind.Error, Loc.FromException(ex));
                _verifySummaryFromLoc = false;
                VerifySummary = Loc.FromException(ex);
                HasVerificationResults = true;
                VerifyAllOk = false;
                return;
            }

            var result = await _verifyService.VerifyAsync(VerifyPdfPath, verifier);
            foreach (var item in result.Signatures)
            {
                VerificationResults.Add(item);
            }

            SelectedVerification = VerificationResults.FirstOrDefault();
            _verifySummaryFromLoc = true;
            VerifySummary = Loc.VerifySummary(VerificationResults);
            HasVerificationResults = true;
            VerifyAllOk = result.Signatures.Count > 0 && result.Signatures.All(s => s.IsOk);
        }
        catch (Exception ex)
        {
            _verifySummaryFromLoc = false;
            VerifySummary = Loc.FromException(ex);
            HasVerificationResults = true;
            VerifyAllOk = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ShowDetailsAsync(object? parameter)
    {
        var check = parameter as SignatureCheck ?? SelectedVerification;
        if (check is null)
        {
            return Task.CompletedTask;
        }

        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current.MainWindow;
        var window = new DetailsWindow(check)
        {
            Owner = owner,
        };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private bool CanCopyDetails(object? parameter)
    {
        var text = parameter as string ?? SelectedVerification?.Details;
        return !string.IsNullOrWhiteSpace(text);
    }

    private Task CopyDetailsAsync(object? parameter)
    {
        var text = parameter as string ?? SelectedVerification?.Details;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    private void RaiseCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        BrowsePdfCommand.RaiseCanExecuteChanged();
        BrowseOutputCommand.RaiseCanExecuteChanged();
        PreviewSignCommand.RaiseCanExecuteChanged();
        SignCommand.RaiseCanExecuteChanged();
        BrowseVerifyCommand.RaiseCanExecuteChanged();
        PreviewVerifyCommand.RaiseCanExecuteChanged();
        VerifyCommand.RaiseCanExecuteChanged();
        CopyDetailsCommand.RaiseCanExecuteChanged();
    }

    private void RefreshLanguage()
    {
        if (!string.IsNullOrWhiteSpace(_rawVersion))
        {
            EimzoVersion = Loc.T("version", _rawVersion);
        }

        if (_eimzoUi != EimzoUiKind.Error)
        {
            SetEimzoStatus(_eimzoUi);
        }

        if (_verifySummaryFromLoc)
        {
            VerifySummary = Loc.VerifySummary(VerificationResults);
        }
    }

    private void SetEimzoStatus(EimzoUiKind kind, string? error = null)
    {
        _eimzoUi = kind;
        EimzoStatus = kind switch
        {
            EimzoUiKind.Connecting => Loc.T("eimzo_connecting"),
            EimzoUiKind.Running => Loc.T("eimzo_running"),
            EimzoUiKind.RunningKeys => Loc.T("eimzo_running_keys", Certificates.Count),
            EimzoUiKind.NoKeys => Loc.T("eimzo_no_keys"),
            EimzoUiKind.Error => error ?? EimzoStatus,
            _ => Loc.T("eimzo_offline"),
        };
    }

    private static string StampIdLabel(EimzoCertificate cert)
    {
        var hasInn = !string.IsNullOrWhiteSpace(cert.Inn);
        var hasPinfl = !string.IsNullOrWhiteSpace(cert.Pinfl);
        if (hasInn && hasPinfl)
        {
            return Loc.T("id_inn_pinfl");
        }

        return hasPinfl && !hasInn ? Loc.T("id_pinfl") : Loc.T("id_inn");
    }

    private enum EimzoUiKind
    {
        Offline,
        Connecting,
        Running,
        RunningKeys,
        NoKeys,
        Error,
    }
}
