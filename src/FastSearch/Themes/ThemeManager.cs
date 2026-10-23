using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FastSearch.Settings;

namespace FastSearch.Themes;

internal static class ThemeManager
{
    private static readonly Uri StylesUri = new("/FastSearch;component/Themes/Controls.xaml", UriKind.Relative);
    internal static AppTheme Current { get; private set; } = AppTheme.LightGray;
    internal static void Ensure()
    {
        var app = Application.Current;
        if (!app.Resources.Contains("Theme.Background")) Apply(AppTheme.LightGray);
    }
    internal static void Apply(AppTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        var app = Application.Current;
        app.Dispatcher.VerifyAccess();
        bool found = false;
        foreach (var dictionary in app.Resources.MergedDictionaries) if (dictionary.Source == StylesUri) found = true;
        if (!found) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri });
        string[] names = ["Background", "Surface", "Header", "Text", "Secondary", "Border", "Accent", "Alternate", "Selection", "SelectedText", "Hover", "Pressed", "Error", "OnAccent"];
        string[] colors = theme == AppTheme.Charcoal
            ? ["#1E232B", "#272E38", "#303946", "#E5EAF0", "#B2BECD", "#465365", "#8CB9F4", "#2B333F", "#364D6C", "#F1F5FA", "#394657", "#42546B", "#FFB4AB", "#1E232B"]
            : ["#E8EDF2", "#F2F5F8", "#DCE3EB", "#202B3A", "#526175", "#BECAD7", "#3868A6", "#EBF0F5", "#D0E1F4", "#202B3A", "#D3DFED", "#C0D3EA", "#A42620", "#FFFFFF"];
        // Replace a fixed set of shared brushes; switching never accumulates dictionaries.
        for (int i = 0; i < names.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            app.Resources["Theme." + names[i]] = brush;
        }
        Current = theme;
        foreach (Window window in app.Windows) UpdateTitleBar(window);
    }
    internal static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => UpdateTitleBar(window);
        UpdateTitleBar(window);
    }
    private static void UpdateTitleBar(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = Current == AppTheme.Charcoal ? 1 : 0;
        // Windows 11 DWM attributes are best effort; client-area theming is independent.
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int caption = ColorRef((SolidColorBrush)Application.Current.Resources["Theme.Header"]);
        int text = ColorRef((SolidColorBrush)Application.Current.Resources["Theme.Text"]);
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }
    private static int ColorRef(SolidColorBrush brush) => brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
