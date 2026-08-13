using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;
using SignPdf.Pdf;

namespace SignPdf.App;

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables;
    private string _language = "ru";

    private Loc()
    {
        _tables = LocTables.All;
        Languages =
        [
            new LanguageOption("ru", "Русский"),
            new LanguageOption("en", "English"),
            new LanguageOption("uz", "O'zbekcha"),
        ];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LanguageOption> Languages { get; }

    public int Version { get; private set; }

    public string Language
    {
        get => _language;
        set => SetLanguage(value);
    }

    public string this[string key] => Get(key);

    public static string T(string key, params object[] args)
    {
        var text = Instance.Get(key);
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    public static string FromException(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null
               && current is not Eimzo.EimzoException
               && current is not Eimzo.EimzoNotRunningException)
        {
            current = current.InnerException;
        }

        if (current is Eimzo.EimzoNotRunningException)
        {
            return T("eimzo_not_running");
        }

        if (current is FileNotFoundException)
        {
            return T("pdf_missing");
        }

        return current.Message;
    }

    public void Load()
    {
        var saved = AppSettings.LoadLanguage();
        if (!string.IsNullOrWhiteSpace(saved) && _tables.ContainsKey(saved))
        {
            _language = saved;
        }
    }

    public void SetLanguage(string? code)
    {
        var next = string.IsNullOrWhiteSpace(code) || !_tables.ContainsKey(code) ? "ru" : code;
        if (next == _language && Version > 0)
        {
            return;
        }

        _language = next;
        AppSettings.SaveLanguage(next);
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private string Get(string key)
    {
        if (_tables.TryGetValue(_language, out var table) && table.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_tables["ru"].TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    public static string Status(SignatureStatus status, bool followedByLaterSignature = false) => status switch
    {
        SignatureStatus.Valid => T("status_valid"),
        SignatureStatus.DocumentModified => T("status_modified"),
        SignatureStatus.CertificateExpired => T("status_expired"),
        SignatureStatus.CertificateNotYetValid => T("status_not_yet"),
        SignatureStatus.SubsequentChanges => followedByLaterSignature ? T("status_later_sig") : T("status_later_edit"),
        SignatureStatus.AlgorithmUnsupported => T("status_algo"),
        SignatureStatus.KeyNotConfirmed => T("status_key"),
        SignatureStatus.NotConfirmed => T("status_unconfirmed"),
        SignatureStatus.Invalid => T("status_invalid"),
        SignatureStatus.Error => T("status_error"),
        _ => status.ToString(),
    };

    public static string VerifySummary(IReadOnlyList<SignatureCheck> checks)
    {
        if (checks.Count == 0)
        {
            return T("verify_none");
        }

        if (checks.All(c => c.Status is SignatureStatus.Valid or SignatureStatus.SubsequentChanges))
        {
            var later = checks.Where(c => c.Status == SignatureStatus.SubsequentChanges).ToList();
            if (later.Count == 0)
            {
                return T("verify_ok");
            }

            return later.All(c => c.FollowedByLaterSignature) ? T("verify_multi") : T("verify_unsigned_append");
        }

        if (checks.Any(c => c.Status == SignatureStatus.KeyNotConfirmed)
            && checks.All(c => c.Status is SignatureStatus.Valid
                or SignatureStatus.SubsequentChanges
                or SignatureStatus.KeyNotConfirmed
                or SignatureStatus.NotConfirmed))
        {
            return T("verify_untrusted");
        }

        if (checks.All(c => c.Status is SignatureStatus.Valid
            or SignatureStatus.SubsequentChanges
            or SignatureStatus.NotConfirmed))
        {
            return T("verify_unclear");
        }

        return T("verify_errors");
    }
}

public sealed class LanguageOption
{
    public LanguageOption(string code, string title)
    {
        Code = code;
        Title = title;
    }

    public string Code { get; }
    public string Title { get; }
}

public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

internal static class AppSettings
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SignPdf", "settings.json");

    public static string? LoadLanguage() => Read()?.Language;

    public static string? LoadTheme() => Read()?.Theme;

    public static void SaveLanguage(string language)
    {
        var settings = Read() ?? new StoredSettings();
        settings.Language = language;
        Write(settings);
    }

    public static void SaveTheme(string theme)
    {
        var settings = Read() ?? new StoredSettings();
        settings.Theme = theme;
        Write(settings);
    }

    private static StoredSettings? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            return new StoredSettings
            {
                Language = root.TryGetProperty("language", out var lang) ? lang.GetString() ?? "ru" : "ru",
                Theme = root.TryGetProperty("theme", out var theme) ? theme.GetString() ?? "light" : "light",
            };
        }
        catch
        {
            return null;
        }
    }

    private static void Write(StoredSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                FilePath,
                "{\"language\":\"" + settings.Language + "\",\"theme\":\"" + settings.Theme + "\"}");
        }
        catch
        {
            // ignored
        }
    }

    private sealed class StoredSettings
    {
        public string Language { get; set; } = "ru";
        public string Theme { get; set; } = "light";
    }
}
