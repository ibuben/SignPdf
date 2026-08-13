using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace SignPdf.App;

internal static class PdfFileAssociation
{
    public const string ProgId = "SignPdf.Document";
    public const string AppRegistryName = "SignPdf";
    private const string CapabilitiesPath = @"Software\SignPdf\Capabilities";

    public static string? FindPdfPath(IReadOnlyList<string> args)
    {
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('-'))
            {
                continue;
            }

            var path = raw.Trim('"');
            if (!File.Exists(path))
            {
                continue;
            }

            return Path.GetFullPath(path);
        }

        return null;
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return;
        }

        var command = $"\"{exe}\" \"%1\"";
        var appIcon = $"\"{exe}\",0";
        var fileIcon = $"\"{EnsurePdfIcon()}\"";

        using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            prog.SetValue("", Loc.T("assoc_type"));
            prog.SetValue("FriendlyTypeName", Loc.T("assoc_type"));
            using var iconKey = prog.CreateSubKey("DefaultIcon");
            iconKey.SetValue("", fileIcon);
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", command);
        }

        using (var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            openWith.SetValue(ProgId, "", RegistryValueKind.String);
        }

        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\SignPdf.exe"))
        {
            app.SetValue("FriendlyAppName", Loc.T("assoc_app"));
            using var cmd = app.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", command);
            using var types = app.CreateSubKey("SupportedTypes");
            types.SetValue(".pdf", "");
        }

        using (var cap = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            cap.SetValue("ApplicationName", Loc.T("assoc_app"));
            cap.SetValue("ApplicationDescription", Loc.T("assoc_desc"));
            cap.SetValue("ApplicationIcon", appIcon);
            using var associations = cap.CreateSubKey("FileAssociations");
            associations.SetValue(".pdf", ProgId);
        }

        using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            apps.SetValue(AppRegistryName, CapabilitiesPath);
        }

        NativeMethods.SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.SHChangeNotify(0x00008000, 0x0003, new IntPtr(-1), IntPtr.Zero);
    }

    private static string EnsurePdfIcon()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SignPdf");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "pdf.ico");
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/pdf.ico"))?.Stream;
        if (stream is not null)
        {
            using (stream)
            using (var file = File.Create(dest))
            {
                stream.CopyTo(file);
            }
        }

        return dest;
    }

    public static bool IsUserDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
            var progId = key?.GetValue("ProgId") as string;
            return string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void OfferAsDefault()
    {
        Register();
        if (IsUserDefault())
        {
            MessageBox.Show(
                Loc.T("assoc_already"),
                Loc.T("assoc_title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        TryLaunchAssociationUi();
        TryOpenDefaultAppsSettings();
        MessageBox.Show(
            Loc.T("assoc_help"),
            Loc.T("assoc_title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static bool TryLaunchAssociationUi()
    {
        try
        {
            var ui = (IApplicationAssociationRegistrationUI)new ApplicationAssociationRegistrationUI();
            ui.LaunchAdvancedAssociationUI(AppRegistryName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryOpenDefaultAppsSettings()
    {
        foreach (var uri in new[] { "ms-settings:defaultapps?fileExt=.pdf", "ms-settings:defaultapps" })
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });
                return;
            }
            catch
            {
                // next uri
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }

    [ComImport]
    [Guid("1968106d-f3b5-44cf-8904-814836d34ddc")]
    private class ApplicationAssociationRegistrationUI
    {
    }

    [ComImport]
    [Guid("1f76a169-f994-40ac-8fc8-0959e8874710")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistrationUI
    {
        void LaunchAdvancedAssociationUI([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName);
    }
}
