using System.Windows;
using SignPdf.Pdf;

namespace SignPdf.App;

public partial class DetailsWindow : Window
{
    private readonly SignatureCheck _check;

    public DetailsWindow(SignatureCheck check)
    {
        _check = check;
        InitializeComponent();
        ApplyLanguage();
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.Instance.PropertyChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyLanguage();

    private void ApplyLanguage()
    {
        Title = string.IsNullOrWhiteSpace(_check.FieldName)
            ? Loc.T("details_title")
            : Loc.T("details_title_field", _check.FieldName);
        ReportBox.Text = Format(_check);
    }

    private static string Format(SignatureCheck check)
    {
        var lines = new List<string>
        {
            Loc.T("field") + ": " + check.FieldName,
            Loc.T("status") + ": " + Loc.Status(check.Status, check.FollowedByLaterSignature),
            Loc.T("signer") + ": " + check.SignerName,
            Loc.T("issuer") + ": " + check.Issuer,
            Loc.T("algorithm") + ": " + check.Algorithm,
            Loc.T("signed_at") + ": " + FormatDate(check.SignedAt, "dd.MM.yyyy HH:mm"),
            Loc.T("key_from") + ": " + FormatDate(check.ValidFrom, "dd.MM.yyyy"),
            Loc.T("key_until") + ": " + FormatDate(check.ValidTo, "dd.MM.yyyy"),
            Loc.T("covers") + ": " + CoverageText(check),
            "",
        };

        if (!string.IsNullOrWhiteSpace(check.Details))
        {
            lines.Add(check.Details);
        }

        if (check.Status == SignatureStatus.SubsequentChanges)
        {
            lines.Add(check.FollowedByLaterSignature ? Loc.T("detail_later") : Loc.T("detail_changed"));
        }

        if (string.IsNullOrWhiteSpace(check.Details)
            || check.Details.IndexOf("PKI", StringComparison.OrdinalIgnoreCase) < 0)
        {
            lines.Add(Loc.T("detail_pki"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string CoverageText(SignatureCheck check)
    {
        if (check.CoversWholeDocument)
        {
            return Loc.T("yes");
        }

        return check.FollowedByLaterSignature ? Loc.T("no_later_sig") : Loc.T("no_other");
    }

    private static string FormatDate(DateTime? value, string format)
    {
        return value is { } date ? date.ToString(format) : Loc.T("dash");
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ReportBox.Text))
        {
            Clipboard.SetText(ReportBox.Text);
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
