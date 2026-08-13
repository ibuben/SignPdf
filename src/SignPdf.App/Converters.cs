using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SignPdf.Eimzo;
using SignPdf.Pdf;

namespace SignPdf.App;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is false;
    }
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class OkToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool ok)
        {
            return Application.Current?.TryFindResource("MutedBrush")
                   ?? System.Windows.Media.Brushes.Gray;
        }

        var key = ok ? "OkBrush" : "DangerBrush";
        return Application.Current?.TryFindResource(key)
               ?? (ok ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Firebrick);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class OkToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool ok)
        {
            return "";
        }

        return ok ? "\uE73E" : "\uE711";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class SignatureStatusLocConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = values.Length > 0 && values[0] is SignatureStatus s ? s : SignatureStatus.Error;
        var later = values.Length > 1 && values[1] is true;
        return Loc.Status(status, later);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class CertificateCaptionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not EimzoCertificate cert)
        {
            return "";
        }

        var who = cert.StampFullName;
        var id = !string.IsNullOrWhiteSpace(cert.Pinfl) ? cert.Pinfl : cert.Inn;
        var until = cert.ValidTo?.ToString("dd.MM.yyyy");
        if (!string.IsNullOrWhiteSpace(id) && until is not null)
        {
            return $"{who}  ·  {id}  ·  {Loc.T("until")} {until}";
        }

        return until is not null ? $"{who}  ·  {Loc.T("until")} {until}" : who;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
