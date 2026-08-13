using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace SignPdf.App;

public sealed class Theme : INotifyPropertyChanged
{
    public static Theme Instance { get; } = new();

    private bool _isDark;

    private Theme()
    {
        Loc.Instance.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleHint)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (_isDark == value)
            {
                return;
            }

            _isDark = value;
            AppSettings.SaveTheme(value ? "dark" : "light");
            Apply();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDark)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleHint)));
        }
    }

    public string Icon => _isDark ? "\uE706" : "\uE708";

    public string ToggleHint => Loc.T(_isDark ? "theme_light" : "theme_dark");

    public void Load()
    {
        _isDark = string.Equals(AppSettings.LoadTheme(), "dark", StringComparison.OrdinalIgnoreCase);
        Apply();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Toggle() => IsDark = !_isDark;

    public void Apply()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (_isDark)
        {
            Set(app, "BgBrush", "#121417");
            Set(app, "CardBrush", "#1C2128");
            Set(app, "ChromeBrush", "#171A1F");
            Set(app, "FieldBrush", "#151920");
            Set(app, "LineBrush", "#2E3440");
            Set(app, "TextBrush", "#E8EDF2");
            Set(app, "MutedBrush", "#9AA4B2");
            Set(app, "AccentBrush", "#2BB89A");
            Set(app, "AccentHoverBrush", "#25A58A");
            Set(app, "DangerBrush", "#F97066");
            Set(app, "OkBrush", "#32D583");
            Set(app, "TabIdleBrush", "#252A32");
            Set(app, "TabHoverBrush", "#2C323C");
            Set(app, "GhostHoverBrush", "#14352F");
            Set(app, "GridAltBrush", "#161B21");
            Set(app, "GridSelectBrush", "#14352F");
            Set(app, "ViewerCanvasBrush", "#0E1013");
        }
        else
        {
            Set(app, "BgBrush", "#F3F5F7");
            Set(app, "CardBrush", "#FFFFFF");
            Set(app, "ChromeBrush", "#FFFFFF");
            Set(app, "FieldBrush", "#FFFFFF");
            Set(app, "LineBrush", "#E4E7EC");
            Set(app, "TextBrush", "#1D2939");
            Set(app, "MutedBrush", "#667085");
            Set(app, "AccentBrush", "#0F6C5C");
            Set(app, "AccentHoverBrush", "#0B584B");
            Set(app, "DangerBrush", "#B42318");
            Set(app, "OkBrush", "#027A48");
            Set(app, "TabIdleBrush", "#EEF1F4");
            Set(app, "TabHoverBrush", "#F8FAFC");
            Set(app, "GhostHoverBrush", "#ECFDF3");
            Set(app, "GridAltBrush", "#F8FAFC");
            Set(app, "GridSelectBrush", "#ECFDF3");
            Set(app, "ViewerCanvasBrush", "#525659");
        }
    }

    private static void Set(Application app, string key, string hex)
    {
        app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
    }
}
